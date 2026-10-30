using System.Diagnostics;
using System.Runtime.InteropServices;
using static WinScope.Modules.Performance.Native;

namespace WinScope.Modules.Performance;

internal enum GpuStatus { Starting, Available, Unavailable }

/// <summary>One app right now, for the "Right now" table and the dashboard card.</summary>
internal readonly record struct AppNow(string Name, int Count, double Cpu, double Gpu, long Ram, double Io, long VRam);

/// <summary>The latest sample, published by the sampler as an immutable object.</summary>
internal sealed class LiveData
{
    public DateTime Utc;
    public Totals Totals;
    public long CommitUsed, CommitLimit;
    public bool HasRates;
    public GpuStatus Gpu;
    public bool DiskAvailable;
    public bool VRamAvailable;
    public AppNow[] Apps = Array.Empty<AppNow>();
    public double SelfCpu;
    public int ProcessCount;
    public int GpuEvery = 1;
    public int Cpus = 1;
    /// <summary>False on a non-64-bit build: per-app, GPU and disk figures are then unavailable.</summary>
    public bool Supported = true;
}

/// <summary>
/// The 1 Hz background collector. Each tick reads: total CPU (GetSystemTimes), memory (GlobalMemoryStatusEx),
/// every process's CPU time / private working set / I/O counters (one NtQuerySystemInformation call – no process
/// handles needed), disk activity and per-process GPU engine utilisation (PDH). It records the second in
/// <see cref="History"/>, feeds the <see cref="SpikeDetector"/> and publishes <see cref="Latest"/>.
/// All native resources belong to the loop and are released in its finally block, so Dispose can never free
/// something the loop is still using.
/// </summary>
internal sealed class Sampler : IDisposable
{
    public History History { get; } = new();
    public SpikeDetector Spikes { get; } = new();
    private volatile LiveData? _latest;
    public LiveData? Latest => _latest;

    /// <summary>Raised on the sampler thread after each sample. Handlers must be quick and must not throw.</summary>
    public event Action? Sampled;

    private readonly CancellationTokenSource _cts = new();
    private Task? _task;
    private int _errors;
    private readonly HashSet<string> _loggedErrors = new();

    public void Start()
    {
        if (_task != null) return;
        var ct = _cts.Token;
        _task = Task.Run(() => Loop(ct));
    }

    /// <summary>Signal the loop to stop now, without waiting (shutdown calls this on every tool first).</summary>
    public void BeginStop()
    {
        try { _cts.Cancel(); } catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        BeginStop();
        // The loop exits at its next await; only a slow native call (a PDH read) can hold it, and it frees its own
        // native resources when it does exit, so a short bounded wait is enough.
        try { _task?.Wait(900); } catch (AggregateException) { }
        // The CTS is not disposed: if the loop is stuck in a slow native call it may still look at the token.
    }

    // ================================================================== loop

    private sealed class State : IDisposable
    {
        public IntPtr ProcBuf;
        public int ProcBufSize;
        public byte[] ProcCopy = new byte[512 * 1024];
        public int ProcLen;
        public readonly List<RawProc> Raw = new(512);
        public readonly ProcessTracker Tracker = new();
        public long PrevIdle, PrevKernel, PrevUser;
        public bool HavePrevTimes;
        public long PrevTimesStamp;      // Stopwatch timestamp of the last successful GetSystemTimes
        public int Cpus;

        public PdhQuery? DiskQ;
        public IntPtr DiskIdle, DiskRead, DiskWrite;
        public readonly PdhArray DiskIdleA = new(), DiskReadA = new(), DiskWriteA = new();

        public PdhQuery? GpuQ;
        public IntPtr GpuEngine, GpuMem, GpuAdapterMem;
        public readonly PdhArray GpuEngineA = new() { Data = new byte[256 * 1024] }, GpuMemA = new(), GpuAdapterMemA = new();
        public readonly List<(int pid, EngineKey eng, double v)> GpuItems = new();
        public readonly List<(int pid, AdapterKey adapter, long bytes)> VRamItems = new();
        public readonly Dictionary<AdapterKey, double> AdapterDedicated = new();
        public readonly Dictionary<AdapterKey, int> AdapterEngines = new();
        public readonly HashSet<EngineKey> EngineSeen = new();
        public readonly GpuAggregator GpuAgg = new();
        public readonly AdapterPicker Picker = new();
        public double GpuOther = double.NaN;
        public GpuStatus Gpu = GpuStatus.Starting;
        public int GpuTries, GpuEvery = 1, Tick;
        public double GpuCostMs;
        public bool GpuLastOk;

        public readonly List<AppAgg> All = new(256);
        public readonly List<AppValue> TopCpu = new(), TopGpu = new(), TopRam = new(), TopIo = new(), AllCpu = new(256);

        public void Dispose()
        {
            try { DiskQ?.Dispose(); } catch { }
            try { GpuQ?.Dispose(); } catch { }
            DiskQ = GpuQ = null;
            if (ProcBuf != IntPtr.Zero) { Marshal.FreeHGlobal(ProcBuf); ProcBuf = IntPtr.Zero; ProcBufSize = 0; }
        }
    }

    private async Task Loop(CancellationToken ct)
    {
        var st = new State();
        try
        {
            Init(st);
            try { SampleOnce(st, prime: true); } catch (Exception ex) { Log(ex); }
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { SampleOnce(st, prime: false); }
                catch (Exception ex) when (!ct.IsCancellationRequested) { Log(ex); }
                if (ct.IsCancellationRequested) break;
                try { Sampled?.Invoke(); } catch (Exception ex) { Log(ex); }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Log(ex); }
        finally { st.Dispose(); }
    }

    private void Init(State st)
    {
        uint n = 0;
        try { n = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS); } catch (EntryPointNotFoundException) { }
        st.Cpus = n > 0 && n <= 4096 ? (int)n : Math.Max(1, Environment.ProcessorCount);

        if (!ProcessParser.Supported)
        {
            // The native layouts we parse are the 64-bit ones; on any other build show "not available" instead.
            st.Gpu = GpuStatus.Unavailable;
            return;
        }

        try
        {
            st.DiskQ = PdhQuery.TryOpen();
            if (st.DiskQ != null)
            {
                // Every physical disk's idle time: we show the busiest disk, because _Total is the *average*
                // and would hide one maxed-out drive behind idle ones.
                st.DiskIdle = st.DiskQ.Add(@"\PhysicalDisk(*)\% Idle Time");
                st.DiskRead = st.DiskQ.Add(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
                st.DiskWrite = st.DiskQ.Add(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
                st.DiskQ.Collect();   // rate counters need a first reading
            }
        }
        catch (Exception ex) { Log(ex); }

        try
        {
            st.GpuQ = PdhQuery.TryOpen();
            if (st.GpuQ != null)
            {
                // WDDM 2.x GPU counters (Windows 10 1709+). Instances are per process per engine.
                st.GpuEngine = st.GpuQ.Add(@"\GPU Engine(*)\Utilization Percentage");
                st.GpuMem = st.GpuQ.Add(@"\GPU Process Memory(*)\Dedicated Usage");
                // Per-adapter dedicated memory in use: tells the discrete card apart from an integrated GPU.
                st.GpuAdapterMem = st.GpuQ.Add(@"\GPU Adapter Memory(*)\Dedicated Usage");
                if (st.GpuEngine == IntPtr.Zero) st.Gpu = GpuStatus.Unavailable;
                else st.GpuQ.Collect();
            }
            else st.Gpu = GpuStatus.Unavailable;
        }
        catch (Exception ex) { Log(ex); st.Gpu = GpuStatus.Unavailable; }
    }

    private void SampleOnce(State st, bool prime)
    {
        // Monotonic timeline stamp (a wall-clock change can't reorder or wipe the history).
        PerfClock.Resync();
        var now = PerfClock.Now;
        st.Tick++;

        var tot = new Totals { Cpu = double.NaN, Gpu = double.NaN, GpuOther = double.NaN, RamPct = double.NaN, Disk = double.NaN, DiskRead = double.NaN, DiskWrite = double.NaN };

        // ---- total CPU (a ratio of the deltas since the last successful call; skipped after sleep/hibernate)
        if (GetSystemTimes(out long idle, out long kernel, out long user))
        {
            long stamp = Stopwatch.GetTimestamp();
            double wall = (stamp - st.PrevTimesStamp) / (double)Stopwatch.Frequency;
            bool sane = wall > 0.05 && wall < 30;
            st.PrevTimesStamp = stamp;
            if (st.HavePrevTimes && sane)
            {
                long dI = idle - st.PrevIdle, dK = kernel - st.PrevKernel, dU = user - st.PrevUser;
                long total = dK + dU;                       // kernel includes idle
                if (total > 0 && dI >= 0) tot.Cpu = Math.Clamp((total - dI) * 100.0 / total, 0, 100);
            }
            st.PrevIdle = idle; st.PrevKernel = kernel; st.PrevUser = user;
            st.HavePrevTimes = true;
        }

        // ---- memory
        long commitUsed = 0, commitLimit = 0;
        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref ms) && ms.ullTotalPhys > 0)
        {
            tot.RamTotal = (long)Math.Min(ms.ullTotalPhys, long.MaxValue);
            tot.RamUsed = (long)Math.Min(ms.ullTotalPhys - Math.Min(ms.ullAvailPhys, ms.ullTotalPhys), long.MaxValue);
            tot.RamPct = tot.RamUsed * 100.0 / tot.RamTotal;
            commitLimit = (long)Math.Min(ms.ullTotalPageFile, long.MaxValue);
            commitUsed = (long)Math.Min(ms.ullTotalPageFile - Math.Min(ms.ullAvailPageFile, ms.ullTotalPageFile), long.MaxValue);
        }

        // ---- processes
        bool procOk = false;
        try
        {
            if (QueryProcesses(st))
            {
                st.Tracker.Update(st.Raw, st.ProcCopy.AsSpan(0, st.ProcLen), Stopwatch.GetTimestamp(), st.Cpus, DateTime.UtcNow.ToFileTimeUtc());
                procOk = true;
            }
        }
        catch (Exception ex) { Log(ex); }

        // ---- disk
        bool diskOk = false;
        try
        {
            if (st.DiskQ != null && st.DiskQ.Collect())
            {
                if (st.DiskQ.Read(st.DiskIdle, st.DiskIdleA))
                {
                    double busiest = double.NaN, totalInst = double.NaN;
                    var span = st.DiskIdleA.Span;
                    foreach (var it in st.DiskIdleA.Items)
                    {
                        double active = Math.Clamp(100 - it.Value, 0, 100);
                        if (it.Name(span).SequenceEqual("_Total")) totalInst = active;
                        else if (double.IsNaN(busiest) || active > busiest) busiest = active;
                    }
                    tot.Disk = double.IsNaN(busiest) ? totalInst : busiest;
                    diskOk = !double.IsNaN(tot.Disk);
                }
                if (st.DiskQ.Read(st.DiskRead, st.DiskReadA) && st.DiskReadA.Items.Count > 0) tot.DiskRead = Math.Max(0, st.DiskReadA.Items[0].Value);
                if (st.DiskQ.Read(st.DiskWrite, st.DiskWriteA) && st.DiskWriteA.Items.Count > 0) tot.DiskWrite = Math.Max(0, st.DiskWriteA.Items[0].Value);
            }
        }
        catch (Exception ex) { Log(ex); }

        // ---- GPU (may be sampled every 2nd/4th second if this PC's GPU counters are slow to read)
        bool vramOk = false;
        try { tot.Gpu = SampleGpu(st, procOk, out vramOk); tot.GpuOther = st.GpuOther; }
        catch (Exception ex) { Log(ex); }

        if (prime) return;

        // ---- per-app lists
        var tr = st.Tracker;
        st.All.Clear();
        if (procOk) foreach (var a in tr.Apps) st.All.Add(a);
        bool rates = procOk && tr.HasRates;
        if (vramOk)
        {
            // Only the reported (main) adapter's memory, so an iGPU's shared carve-out isn't added in.
            var primary = st.Picker.Current;
            foreach (var (pid, adapter, bytes) in st.VRamItems)
                if ((primary == null || adapter == primary.Value) && tr.AppForPid(pid) is AppAgg app) app.VRam += bytes;
        }

        Top(st.All, st.TopRam, History.TopRamN, a => a.Ram, 1);
        if (rates)
        {
            Top(st.All, st.TopCpu, History.TopCpuN, a => a.Cpu, 0.1);
            Top(st.All, st.TopIo, History.TopIoN, a => a.Io, 1);
        }
        else { st.TopCpu.Clear(); st.TopIo.Clear(); }
        if (st.Gpu == GpuStatus.Available) Top(st.All, st.TopGpu, History.TopGpuN, a => a.Gpu, 0.1);
        else st.TopGpu.Clear();

        st.AllCpu.Clear();
        if (rates) foreach (var a in st.All) st.AllCpu.Add(new AppValue(a.Name, a.Cpu));

        History.Add(now, tot, st.TopCpu, st.TopGpu, st.TopRam, st.TopIo);
        Spikes.Feed(now, tot.Cpu, tot.RamPct, tot.Disk, st.TopCpu, st.TopRam, st.TopIo, rates ? st.AllCpu : null, st.Cpus);

        var apps = new AppNow[st.All.Count];
        for (int i = 0; i < apps.Length; i++)
        {
            var a = st.All[i];
            apps[i] = new AppNow(a.Name, a.Count, rates ? a.Cpu : double.NaN,
                st.Gpu == GpuStatus.Available ? a.Gpu : double.NaN, a.Ram, rates ? a.Io : double.NaN, vramOk ? a.VRam : -1);
        }
        _latest = new LiveData
        {
            Utc = now, Totals = tot, CommitUsed = commitUsed, CommitLimit = commitLimit, HasRates = rates,
            Gpu = st.Gpu, DiskAvailable = diskOk, VRamAvailable = vramOk, Apps = apps,
            SelfCpu = rates ? tr.SelfCpu : double.NaN, ProcessCount = procOk ? tr.ProcessCount : 0, GpuEvery = st.GpuEvery,
            Cpus = st.Cpus, Supported = ProcessParser.Supported,
        };
    }

    private static void Top(List<AppAgg> all, List<AppValue> dest, int n, Func<AppAgg, double> key, double min)
    {
        dest.Clear();
        // n is tiny (≤ 8): a simple insertion into a short sorted list beats sorting ~150 apps.
        foreach (var a in all)
        {
            double v = key(a);
            if (!(v >= min)) continue;
            int pos = dest.Count;
            while (pos > 0 && dest[pos - 1].Value < v) pos--;
            if (pos >= n) continue;
            dest.Insert(pos, new AppValue(a.Name, v));
            if (dest.Count > n) dest.RemoveAt(n);
        }
    }

    // ================================================================== processes

    private const int MaxProcBuffer = 64 * 1024 * 1024;

    /// <summary>
    /// One NtQuerySystemInformation(SystemProcessInformation) call into a reused native buffer that grows on
    /// STATUS_INFO_LENGTH_MISMATCH (the process list can grow between calls, so retry a few times).
    /// The returned block is copied to a managed array and parsed there with bounds checks.
    /// </summary>
    private static bool QueryProcesses(State st)
    {
        if (st.ProcBuf == IntPtr.Zero) GrowProc(st, 512 * 1024);
        for (int attempt = 0; attempt < 5; attempt++)
        {
            int rc = NtQuerySystemInformation(SystemProcessInformation, st.ProcBuf, st.ProcBufSize, out int ret);
            if (rc == STATUS_INFO_LENGTH_MISMATCH || rc == STATUS_BUFFER_TOO_SMALL)
            {
                long want = Math.Max((long)Math.Max(0, ret) + 128 * 1024, (long)st.ProcBufSize * 3 / 2);
                if (want > MaxProcBuffer) return false;
                GrowProc(st, (int)want);
                continue;
            }
            if (rc != STATUS_SUCCESS) return false;
            int len = ret > 0 && ret <= st.ProcBufSize ? ret : st.ProcBufSize;
            if (st.ProcCopy.Length < len) st.ProcCopy = new byte[Math.Max(len, st.ProcCopy.Length * 3 / 2)];
            Marshal.Copy(st.ProcBuf, st.ProcCopy, 0, len);
            st.ProcLen = len;
            ProcessParser.Parse(st.ProcCopy.AsSpan(0, len), (ulong)(long)st.ProcBuf, st.Raw);
            return st.Raw.Count > 0;
        }
        return false;
    }

    private static void GrowProc(State st, int size)
    {
        if (st.ProcBuf != IntPtr.Zero) { Marshal.FreeHGlobal(st.ProcBuf); st.ProcBuf = IntPtr.Zero; st.ProcBufSize = 0; }
        st.ProcBuf = Marshal.AllocHGlobal(size);
        st.ProcBufSize = size;
    }

    // ================================================================== GPU

    private double SampleGpu(State st, bool procOk, out bool vramOk)
    {
        vramOk = false;
        st.GpuOther = double.NaN;
        var q = st.GpuQ;
        if (q == null || st.GpuEngine == IntPtr.Zero) { st.Gpu = GpuStatus.Unavailable; return double.NaN; }
        // While unavailable, only look again every 15 s (counters can appear after a driver install).
        if (st.Gpu == GpuStatus.Unavailable && st.Tick % 15 != 0) return double.NaN;

        bool due = st.Tick % st.GpuEvery == 0 || st.Gpu != GpuStatus.Available;
        if (due)
        {
            long t0 = Stopwatch.GetTimestamp();
            bool ok = q.Collect() && q.Read(st.GpuEngine, st.GpuEngineA);
            st.GpuItems.Clear();
            st.EngineSeen.Clear();
            st.AdapterEngines.Clear();
            if (ok)
            {
                var span = st.GpuEngineA.Span;
                foreach (var it in st.GpuEngineA.Items)
                    if (PdhParser.TryParseGpuEngine(it.Name(span), out int pid, out var eng))
                    {
                        st.GpuItems.Add((pid, eng, it.Value));
                        if (st.EngineSeen.Add(eng)) st.AdapterEngines[eng.Adapter] = st.AdapterEngines.GetValueOrDefault(eng.Adapter) + 1;
                    }
            }
            st.VRamItems.Clear();
            st.AdapterDedicated.Clear();
            if (ok && st.GpuMem != IntPtr.Zero && q.Read(st.GpuMem, st.GpuMemA))
            {
                var span = st.GpuMemA.Span;
                foreach (var it in st.GpuMemA.Items)
                    if (PdhParser.TryParseProcessMemory(it.Name(span), out int pid, out var adapter) && pid > 0 && it.Value > 0)
                        st.VRamItems.Add((pid, adapter, (long)Math.Min(it.Value, 1e15)));
            }
            if (ok && st.GpuAdapterMem != IntPtr.Zero && q.Read(st.GpuAdapterMem, st.GpuAdapterMemA))
            {
                var span = st.GpuAdapterMemA.Span;
                foreach (var it in st.GpuAdapterMemA.Items)
                    if (PdhParser.TryParseAdapter(it.Name(span), out var adapter) && it.Value > 0)
                        st.AdapterDedicated[adapter] = st.AdapterDedicated.GetValueOrDefault(adapter) + it.Value;
            }
            if (st.AdapterDedicated.Count == 0)   // no adapter counter: sum the per-process figures instead
                foreach (var (_, adapter, bytes) in st.VRamItems)
                    st.AdapterDedicated[adapter] = st.AdapterDedicated.GetValueOrDefault(adapter) + bytes;
            if (ok) st.Picker.Pick(st.AdapterDedicated, st.AdapterEngines);

            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            st.GpuCostMs = st.GpuCostMs <= 0 ? ms : st.GpuCostMs * 0.8 + ms * 0.2;
            // Keep the sampler cheap: on PCs with thousands of GPU engine instances, read them less often.
            st.GpuEvery = st.GpuCostMs > 80 ? 4 : st.GpuCostMs > 35 ? 2 : st.GpuCostMs < 15 ? 1 : st.GpuEvery;

            if (ok && st.GpuItems.Count > 0) st.Gpu = GpuStatus.Available;
            else if (st.Gpu != GpuStatus.Available && ++st.GpuTries >= 10) st.Gpu = GpuStatus.Unavailable;
            st.GpuLastOk = ok;
        }
        if (st.Gpu != GpuStatus.Available || !st.GpuLastOk) return double.NaN;   // a failed read is "unknown", not 0 %
        vramOk = st.VRamItems.Count > 0;
        var tracker = st.Tracker;
        // Re-attribute on every sample (even when the counters weren't re-read) so per-app values stay current.
        double total = st.GpuAgg.Aggregate(st.GpuItems, procOk ? pid => tracker.AppForPid(pid) : _ => null,
            st.Picker.Current, out double other);
        st.GpuOther = other;
        return total;
    }

    // ================================================================== errors

    private void Log(Exception ex)
    {
        // Something failing every second must not fill the log: each distinct error once, and at most 20 overall.
        lock (_loggedErrors)
        {
            if (_errors >= 20 || !_loggedErrors.Add(ex.GetType().Name + ex.Message)) return;
            _errors++;
        }
        try { App.LogError(ex); } catch { }
    }
}
