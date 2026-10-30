namespace WinScope.Modules.Performance;

/// <summary>Per-app totals for one sample (all processes sharing an image name, e.g. "chrome.exe ×24").</summary>
internal sealed class AppAgg
{
    public AppAgg(string name) { Name = name; }
    public string Name { get; }
    public int Count;
    public double Cpu;          // % of the whole machine (all logical CPUs = 100)
    public double Gpu;          // % (max over engines), filled by GpuAggregator
    public long Ram;            // private working set, bytes
    public double Io;           // bytes/s, all I/O (disk + network + other)
    public long VRam;           // dedicated GPU memory, bytes (if available)
    public bool HasRates;       // false until we have two samples of at least one process

    public void Reset() { Count = 0; Cpu = 0; Gpu = 0; Ram = 0; Io = 0; VRam = 0; HasRates = false; }
}

/// <summary>
/// Turns successive process snapshots into per-process rates and per-app totals. Processes are tracked by
/// PID + CreateTime, so when Windows reuses a PID the new process starts fresh instead of inheriting (or
/// producing a huge negative/positive delta from) the old one's counters.
/// Not thread-safe: used only by the sampler thread.
/// </summary>
internal sealed class ProcessTracker
{
    private sealed class PState
    {
        public long Create;
        public string Name = "";
        public AppAgg App = null!;
        public long Cpu, Io;
        public int Gen;
    }

    private readonly Dictionary<long, PState> _procs = new();
    private readonly Dictionary<string, AppAgg> _apps = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<long> _dead = new();
    private readonly List<string> _deadApps = new();
    private int _gen;
    private long _prevFileTime;           // system time (FILETIME) of the previous snapshot, 0 = none yet
    private long _prevStamp;              // Stopwatch timestamp of the previous successful Update, 0 = none yet
    private readonly long _selfPid = Environment.ProcessId;

    /// <summary>Apps present in the last snapshot.</summary>
    public IReadOnlyCollection<AppAgg> Apps => _apps.Values;
    /// <summary>True once rates are available (second snapshot onwards).</summary>
    public bool HasRates { get; private set; }
    /// <summary>WinScope's own CPU %, for the "WinScope uses …" readout.</summary>
    public double SelfCpu { get; private set; }
    public int ProcessCount { get; private set; }

    public AppAgg? AppForPid(int pid) => _procs.TryGetValue(pid, out var s) && s.Gen == _gen ? s.App : null;

    /// <summary>
    /// Fold in a snapshot taken at Stopwatch timestamp <paramref name="stamp"/> (wall clock
    /// <paramref name="nowFileTime"/>). Rates are computed over the time since the previous *successful* Update,
    /// so a missed snapshot never divides two seconds of CPU time by one. <paramref name="buf"/> is the buffer the
    /// raw entries were parsed from (for reading new names).
    /// </summary>
    public void Update(List<RawProc> raw, ReadOnlySpan<byte> buf, long stamp, int logicalCpus, long nowFileTime)
    {
        _gen++;
        double wallSeconds = _prevStamp == 0 ? 0 : (stamp - _prevStamp) / (double)System.Diagnostics.Stopwatch.Frequency;
        _prevStamp = stamp;
        // After sleep/hibernate (or a stall over 30 s) the interval is meaningless: start afresh.
        bool rates = _prevFileTime != 0 && wallSeconds > 0.05 && wallSeconds < 30;
        double cpuDenom = wallSeconds * 1e7 * Math.Max(1, logicalCpus);    // 100 ns units across all CPUs
        foreach (var a in _apps.Values) a.Reset();
        SelfCpu = 0;
        int count = 0;

        foreach (var p in raw)
        {
            if (p.Pid == 0) continue;                                     // System Idle Process – not an app
            count++;
            bool fresh = !_procs.TryGetValue(p.Pid, out var s) || s.Create != p.CreateTime;
            if (fresh)
            {
                string name = ProcessParser.ReadName(buf, p) ?? (p.Pid == 4 ? "System" : $"pid {p.Pid}");
                s = new PState { Create = p.CreateTime, Name = name, App = GetApp(name), Cpu = p.CpuTime, Io = p.IoBytes };
                _procs[p.Pid] = s;
            }
            // (An existing process's app can't have been pruned: apps are only dropped when no live process uses them.)
            s!.Gen = _gen;
            var app = s.App;
            app.Count++;
            app.Ram += p.PrivateWs;

            if (rates)
            {
                long dCpu, dIo;
                if (!fresh) { dCpu = p.CpuTime - s.Cpu; dIo = p.IoBytes - s.Io; }
                else if (p.CreateTime >= _prevFileTime)
                {
                    // Started since the last snapshot: everything it has done happened in this interval.
                    dCpu = p.CpuTime; dIo = p.IoBytes;
                }
                else { dCpu = 0; dIo = 0; }                                // existed before but we missed it
                if (dCpu < 0) dCpu = 0;
                if (dIo < 0) dIo = 0;
                double cpu = Math.Min(100, dCpu / cpuDenom * 100);
                app.Cpu += cpu;
                app.Io += dIo / wallSeconds;
                app.HasRates = true;
                if (p.Pid == _selfPid) SelfCpu = cpu;
            }
            s.Cpu = p.CpuTime;
            s.Io = p.IoBytes;
        }

        // Forget processes that have exited, and apps with no processes left.
        _dead.Clear();
        foreach (var (pid, s) in _procs) if (s.Gen != _gen) _dead.Add(pid);
        foreach (var pid in _dead) _procs.Remove(pid);
        _deadApps.Clear();
        foreach (var (n, a) in _apps) { if (a.Count == 0) _deadApps.Add(n); else if (a.Cpu > 100) a.Cpu = 100; }
        foreach (var n in _deadApps) _apps.Remove(n);

        ProcessCount = count;
        HasRates = rates;
        _prevFileTime = nowFileTime;
    }

    private AppAgg GetApp(string name)
    {
        if (!_apps.TryGetValue(name, out var a)) { a = new AppAgg(name); _apps[name] = a; }
        return a;
    }
}
