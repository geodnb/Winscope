namespace WinScope.Modules.Performance;

internal enum SpikeKind { Cpu, Memory, Disk, Burst }

/// <summary>An app blamed for a spike: average and peak during it (CPU %, RAM bytes or I/O bytes/s).</summary>
internal readonly record struct Culprit(string Name, double Avg, double Max);

/// <summary>Immutable copy of a spike for the UI.</summary>
internal sealed record SpikeInfo(int Id, SpikeKind Kind, DateTime StartUtc, DateTime EndUtc, DateTime PeakUtc,
    double Peak, string? App, Culprit[] Culprits, bool Ongoing, int Cpus)
{
    public TimeSpan Duration => EndUtc - StartUtc + TimeSpan.FromSeconds(1);   // each sample covers ~1 s
    /// <summary>A CPU % of the whole machine expressed as busy logical processors ("cores").</summary>
    public double Cores(double pctOfMachine) => pctOfMachine / 100 * Math.Max(1, Cpus);
}

/// <summary>
/// Watches the per-second totals and flags the moments worth looking back at:
///  • CPU ≥ 85 % for ≥ 3 s       • memory ≥ 90 % for ≥ 5 s       • disk active ≥ 90 % for ≥ 5 s
///  • a "burst": one app going from &lt; 0.5 cores to ≥ 2 cores within 2 s (e.g. a shader compile or an updater
///    waking up – the classic cause of a game hitch even when total CPU isn't pegged).
/// The GPU being busy is deliberately not flagged: games keep it near 99 %, that's normal.
/// Spikes of the same kind (and, for bursts, the same app) that overlap or follow within a few seconds are merged.
/// Keeps the last 200 spikes. Fed by the sampler thread; <see cref="Snapshot"/> may be called from any thread.
/// </summary>
internal sealed class SpikeDetector
{
    public const double CpuThreshold = 85, MemThreshold = 90, DiskThreshold = 90;
    public const int CpuMinSamples = 3, MemMinSamples = 5, DiskMinSamples = 5;
    /// <summary>
    /// Bursts are measured in cores (busy logical processors), not % of the machine: on a 32-thread CPU, 40 % would
    /// need 13 busy threads, yet one updater or antivirus thread pegging 2 cores is exactly what causes a hitch.
    /// An app bursts when it goes from &lt; 0.5 cores to ≥ 2 cores (or ≥ 40 % of the machine, if that's less – i.e.
    /// on PCs with fewer than 5 logical processors) within 2 s, and carries on while it stays at half that.
    /// </summary>
    public const double BurstFromCores = 0.5, BurstToCores = 2, BurstToMachinePct = 40;
    public const int MaxSpikes = 200;
    private static readonly TimeSpan MergeGap = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxSampleGap = TimeSpan.FromSeconds(2.5);   // longer gap = the run is broken
    private static readonly TimeSpan BurstWindow = TimeSpan.FromSeconds(2.5);    // "within 2 s" with 1 s jitter slack

    private sealed class Live
    {
        public int Id;
        public SpikeKind Kind;
        public string? App;
        public DateTime Start, End, PeakT;
        public double Peak;
        public int Samples;
        public readonly Dictionary<string, (double sum, double max)> Blame = new(StringComparer.OrdinalIgnoreCase);
        public bool Ongoing;
        public bool Published;
        public int Cpus;
        public Culprit[]? Cached;      // culprit list, computed once until the spike changes again
    }

    /// <summary>A run of consecutive over-threshold samples, possibly attached to a (published) spike.</summary>
    private sealed class Run
    {
        public DateTime Start, Last;
        public int Count;
        public Live? Spike;
        public Live Pending = new();   // accumulates until the run qualifies
    }

    private readonly object _lock = new();
    private readonly LinkedList<Live> _spikes = new();      // newest first
    private Run? _cpu, _mem, _disk;
    private readonly Dictionary<string, Run> _bursts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (double v1, DateTime t1, double v2, DateTime t2)> _prev = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _scratch = new();
    private DateTime _lastAppSample;
    private int _cpus = 1;
    private double _burstTo = BurstToMachinePct, _burstFrom = 5, _burstHold = 20;   // % of machine, set per Feed
    private int _appSamples;
    private int _nextId = 1;
    private int _version;

    public int Version => Volatile.Read(ref _version);

    /// <summary>
    /// Feed one second. <paramref name="apps"/> is every app's CPU % (for burst detection) or null when per-app
    /// rates aren't available yet; the top lists are used to name culprits.
    /// </summary>
    public void Feed(DateTime t, double cpu, double ramPct, double diskPct,
        IReadOnlyList<AppValue> topCpu, IReadOnlyList<AppValue> topRam, IReadOnlyList<AppValue> topIo,
        IReadOnlyList<AppValue>? apps, int logicalCpus)
    {
        lock (_lock)
        {
            _cpus = Math.Max(1, logicalCpus);
            double perCore = 100.0 / _cpus;
            _burstTo = Math.Min(BurstToCores * perCore, BurstToMachinePct);
            _burstFrom = Math.Min(BurstFromCores * perCore, _burstTo / 4);
            _burstHold = _burstTo / 2;
            bool changed = false;
            changed |= Step(ref _cpu, SpikeKind.Cpu, null, t, cpu, CpuThreshold, CpuMinSamples, topCpu);
            changed |= Step(ref _mem, SpikeKind.Memory, null, t, ramPct, MemThreshold, MemMinSamples, topRam);
            changed |= Step(ref _disk, SpikeKind.Disk, null, t, diskPct, DiskThreshold, DiskMinSamples, topIo);
            if (apps != null) changed |= Bursts(t, apps);
            if (changed) _version++;
        }
    }

    private bool Bursts(DateTime t, IReadOnlyList<AppValue> apps)
    {
        bool changed = false;
        bool havePrev = _appSamples > 0 && t - _lastAppSample <= MaxSampleGap;
        // Continue/close running bursts first (an app missing from this sample counts as 0 %).
        _scratch.Clear();
        foreach (var (name, _) in _bursts) _scratch.Add(name);
        foreach (var name in _scratch)
        {
            double v = 0;
            foreach (var a in apps) if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) { v = a.Value; break; }
            var run = _bursts[name];
            if (v >= _burstHold && t - run.Last <= MaxSampleGap)
            {
                run.Last = t; run.Count++;
                Accumulate(run.Spike!, t, v, new[] { new AppValue(name, v) });
                changed = true;
            }
            else
            {
                run.Spike!.Ongoing = false;
                _bursts.Remove(name);
                changed = true;
            }
        }

        foreach (var a in apps)
        {
            if (a.Value < _burstTo || _bursts.ContainsKey(a.Name) || !havePrev) continue;
            // Was this app below 5 % one or two samples ago (absent = 0 %)?
            bool wasLow;
            if (_prev.TryGetValue(a.Name, out var p))
                wasLow = (t - p.t1 <= BurstWindow && p.v1 < _burstFrom) || (t - p.t2 <= BurstWindow && p.v2 < _burstFrom)
                         || t - p.t1 > BurstWindow;   // not seen recently → it was idle/absent
            else wasLow = true;
            if (!wasLow) continue;
            var run = new Run { Start = t, Last = t, Count = 1 };
            run.Spike = Attach(SpikeKind.Burst, a.Name, t) ?? Publish(SpikeKind.Burst, a.Name, t, run.Pending);
            Accumulate(run.Spike, t, a.Value, new[] { new AppValue(a.Name, a.Value) });
            _bursts[a.Name] = run;
            changed = true;
        }

        // Remember the last two samples per app (apps that vanished drop out after the window).
        foreach (var a in apps)
        {
            if (_prev.TryGetValue(a.Name, out var p)) _prev[a.Name] = (a.Value, t, p.v1, p.t1);
            else _prev[a.Name] = (a.Value, t, 0, t - BurstWindow - BurstWindow);
        }
        _scratch.Clear();
        foreach (var (name, p) in _prev) if (t - p.t1 > BurstWindow + BurstWindow) _scratch.Add(name);
        foreach (var n in _scratch) _prev.Remove(n);
        _lastAppSample = t;
        _appSamples++;
        return changed;
    }

    private bool Step(ref Run? run, SpikeKind kind, string? app, DateTime t, double value, double threshold, int minSamples,
        IReadOnlyList<AppValue> blame)
    {
        bool changed = false;
        if (run != null && t - run.Last > MaxSampleGap) { changed = run.Spike != null; Close(run); run = null; }
        if (double.IsNaN(value) || value < threshold)
        {
            if (run == null) return changed;
            changed |= run.Spike != null;
            Close(run);
            run = null;
            return changed;
        }
        if (run == null)
        {
            run = new Run { Start = t, Last = t };
            run.Spike = Attach(kind, app, t);          // right after a spike of the same kind → just carry it on
        }
        run.Last = t;
        run.Count++;
        var target = run.Spike ?? run.Pending;
        Accumulate(target, t, value, blame);
        if (run.Spike == null && run.Count >= minSamples)
        {
            run.Spike = Publish(kind, app, run.Start, run.Pending);
            return true;
        }
        return run.Spike != null;
    }

    private static void Close(Run run)
    {
        if (run.Spike != null) run.Spike.Ongoing = false;
    }

    private static void Accumulate(Live s, DateTime t, double value, IReadOnlyList<AppValue> blame)
    {
        if (s.Samples == 0) s.Start = t;
        s.Cached = null;
        s.End = t;
        s.Samples++;
        if (value > s.Peak || s.Samples == 1) { s.Peak = value; s.PeakT = t; }
        foreach (var b in blame)
        {
            var cur = s.Blame.GetValueOrDefault(b.Name);
            s.Blame[b.Name] = (cur.sum + b.Value, Math.Max(cur.max, b.Value));
        }
        // Keep the accumulator small: only the strongest few dozen names matter.
        if (s.Blame.Count > 40)
        {
            var keep = s.Blame.OrderByDescending(kv => kv.Value.sum).Take(20).ToList();
            s.Blame.Clear();
            foreach (var kv in keep) s.Blame[kv.Key] = kv.Value;
        }
    }

    /// <summary>The newest spike of this kind/app if it ended within the merge gap, re-opened.</summary>
    private Live? Attach(SpikeKind kind, string? app, DateTime t)
    {
        foreach (var s in _spikes)
        {
            if (s.Kind != kind || !string.Equals(s.App, app, StringComparison.OrdinalIgnoreCase)) continue;
            if (t - s.End <= MergeGap) { s.Ongoing = true; return s; }
            return null;   // only the newest one of this kind can be adjacent
        }
        return null;
    }

    private Live Publish(SpikeKind kind, string? app, DateTime start, Live acc)
    {
        acc.Id = _nextId++;
        acc.Kind = kind;
        acc.App = app;
        if (acc.Samples == 0) { acc.Start = start; acc.End = start; acc.PeakT = start; }
        acc.Ongoing = true;
        acc.Published = true;
        acc.Cpus = _cpus;
        _spikes.AddFirst(acc);
        while (_spikes.Count > MaxSpikes) _spikes.RemoveLast();
        return acc;
    }

    /// <summary>Copy of the spike log, newest first.</summary>
    public SpikeInfo[] Snapshot()
    {
        lock (_lock)
        {
            var res = new SpikeInfo[_spikes.Count];
            int i = 0;
            foreach (var s in _spikes)
            {
                int n = Math.Max(1, s.Samples);
                s.Cached ??= s.Blame.OrderByDescending(kv => kv.Value.sum).Take(3)
                    .Select(kv => new Culprit(kv.Key, kv.Value.sum / n, kv.Value.max)).ToArray();
                res[i++] = new SpikeInfo(s.Id, s.Kind, s.Start, s.End, s.PeakT, s.Peak, s.App, s.Cached, s.Ongoing, s.Cpus);
            }
            return res;
        }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _spikes.Clear();
            _cpu = _mem = _disk = null;
            _bursts.Clear();
            _prev.Clear();
            _appSamples = 0;
            _version++;
        }
    }
}
