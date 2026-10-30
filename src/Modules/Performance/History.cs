namespace WinScope.Modules.Performance;

/// <summary>An app and one value (CPU %, GPU %, RAM bytes or I/O bytes/s depending on the list).</summary>
internal readonly record struct AppValue(string Name, double Value);

/// <summary>Machine-wide figures for one second.</summary>
internal struct Totals
{
    public double Cpu, Gpu, RamPct, Disk, DiskRead, DiskWrite;  // %, %, %, % active, bytes/s, bytes/s (NaN = unknown)
    /// <summary>Busiest engine on any *other* graphics adapter (e.g. an integrated GPU), NaN if none.</summary>
    public double GpuOther;
    public long RamUsed, RamTotal;
}

/// <summary>Everything recorded for one second: totals plus that second's top apps.</summary>
internal sealed class Moment
{
    public DateTime Utc;
    public Totals Totals;
    public AppValue[] TopCpu = Array.Empty<AppValue>();
    public AppValue[] TopGpu = Array.Empty<AppValue>();
    public AppValue[] TopRam = Array.Empty<AppValue>();
    public AppValue[] TopIo = Array.Empty<AppValue>();
}

/// <summary>Reusable output for <see cref="History.CopySeries"/> (sized for the whole history, so no reallocation).</summary>
internal sealed class SeriesBuffer
{
    public readonly long[] T = new long[History.Capacity];
    public readonly float[] Cpu = new float[History.Capacity];
    public readonly float[] Gpu = new float[History.Capacity];
    public readonly float[] Ram = new float[History.Capacity];
    public readonly float[] Disk = new float[History.Capacity];
    public int Count;
}

/// <summary>
/// Fixed-size ring buffer: 2 hours at one sample per second, with each second's top apps stored in flat
/// preallocated arrays (≈3 MB in total, no per-sample allocations). App names are shared string references.
/// Written by the sampler thread, read by the UI thread; a single lock keeps reads consistent.
/// </summary>
internal sealed class History
{
    public const int Seconds = 2 * 60 * 60;
    public const int Capacity = Seconds + 120;
    public const int TopCpuN = 8, TopGpuN = 5, TopRamN = 5, TopIoN = 5;
    private const int OffCpu = 0, OffGpu = OffCpu + TopCpuN, OffRam = OffGpu + TopGpuN, OffIo = OffRam + TopRamN;
    public const int Slots = OffIo + TopIoN;

    private readonly object _lock = new();
    private readonly long[] _t = new long[Capacity];             // UTC ticks, strictly increasing
    private readonly Totals[] _tot = new Totals[Capacity];
    private readonly string?[] _names = new string?[Capacity * Slots];
    private readonly float[] _vals = new float[Capacity * Slots];
    private int _head;                                             // next write position
    private int _count;
    private int _version;

    public int Count { get { lock (_lock) return _count; } }
    public int Version => Volatile.Read(ref _version);

    public DateTime? OldestUtc
    {
        get { lock (_lock) return _count == 0 ? null : new DateTime(_t[Phys(0)], DateTimeKind.Utc); }
    }

    public DateTime? NewestUtc
    {
        get { lock (_lock) return _count == 0 ? null : new DateTime(_t[Phys(_count - 1)], DateTimeKind.Utc); }
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0; _count = 0;
            Array.Clear(_names);
            _version++;
        }
    }

    /// <summary>
    /// Append one second. The top lists must already be sorted (largest first); extra entries are ignored.
    /// Stamps come from <see cref="PerfClock"/> (monotonic), so they only go forwards; should one ever not be later
    /// than the previous sample it is nudged forwards rather than breaking the order.
    /// </summary>
    public void Add(DateTime utc, in Totals totals, IReadOnlyList<AppValue> cpu, IReadOnlyList<AppValue> gpu,
        IReadOnlyList<AppValue> ram, IReadOnlyList<AppValue> io)
    {
        lock (_lock)
        {
            long t = utc.Ticks;
            if (_count > 0)
            {
                long last = _t[Phys(_count - 1)];
                if (t <= last) t = last + 1;
            }
            int i = _head;
            _t[i] = t;
            _tot[i] = totals;
            Put(i, OffCpu, TopCpuN, cpu);
            Put(i, OffGpu, TopGpuN, gpu);
            Put(i, OffRam, TopRamN, ram);
            Put(i, OffIo, TopIoN, io);
            _head = (_head + 1) % Capacity;
            if (_count < Capacity) _count++;
            _version++;
        }
    }

    private void Put(int i, int off, int n, IReadOnlyList<AppValue> src)
    {
        int b = i * Slots + off;
        for (int k = 0; k < n; k++)
        {
            if (k < src.Count) { _names[b + k] = src[k].Name; _vals[b + k] = (float)src[k].Value; }
            else { _names[b + k] = null; _vals[b + k] = 0; }
        }
    }

    private int Phys(int logical) => ((_head - _count + logical) % Capacity + Capacity) % Capacity;

    /// <summary>First logical index with time ≥ ticks (or _count). Caller holds the lock.</summary>
    private int LowerBound(long ticks)
    {
        int lo = 0, hi = _count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_t[Phys(mid)] < ticks) lo = mid + 1; else hi = mid;
        }
        return lo;
    }

    /// <summary>Copy the totals series for [from, to] (inclusive, UTC) into <paramref name="buf"/>.</summary>
    public void CopySeries(DateTime fromUtc, DateTime toUtc, SeriesBuffer buf)
    {
        lock (_lock)
        {
            int a = LowerBound(fromUtc.Ticks);
            int n = 0;
            for (int j = a; j < _count && n < buf.T.Length; j++)
            {
                int p = Phys(j);
                if (_t[p] > toUtc.Ticks) break;
                ref readonly var tt = ref _tot[p];
                buf.T[n] = _t[p];
                buf.Cpu[n] = (float)tt.Cpu;
                buf.Gpu[n] = (float)tt.Gpu;
                buf.Ram[n] = (float)tt.RamPct;
                buf.Disk[n] = (float)tt.Disk;
                n++;
            }
            buf.Count = n;
        }
    }

    /// <summary>The recorded second nearest to <paramref name="utc"/>, if one is within <paramref name="maxDistance"/>.</summary>
    public Moment? GetMoment(DateTime utc, TimeSpan maxDistance)
    {
        lock (_lock)
        {
            if (_count == 0) return null;
            long t = utc.Ticks;
            int j = LowerBound(t);
            int best = -1; long bestD = long.MaxValue;
            for (int k = j - 1; k <= j; k++)
            {
                if (k < 0 || k >= _count) continue;
                long d = Math.Abs(_t[Phys(k)] - t);
                if (d < bestD) { bestD = d; best = k; }
            }
            if (best < 0 || bestD > maxDistance.Ticks) return null;
            int p = Phys(best);
            return new Moment
            {
                Utc = new DateTime(_t[p], DateTimeKind.Utc),
                Totals = _tot[p],
                TopCpu = Get(p, OffCpu, TopCpuN),
                TopGpu = Get(p, OffGpu, TopGpuN),
                TopRam = Get(p, OffRam, TopRamN),
                TopIo = Get(p, OffIo, TopIoN),
            };
        }
    }

    private AppValue[] Get(int p, int off, int n)
    {
        int b = p * Slots + off;
        int c = 0;
        while (c < n && _names[b + c] != null) c++;
        if (c == 0) return Array.Empty<AppValue>();
        var r = new AppValue[c];
        for (int k = 0; k < c; k++) r[k] = new AppValue(_names[b + k]!, _vals[b + k]);
        return r;
    }

    /// <summary>
    /// Up to the last <paramref name="n"/> samples of one series from the last n seconds, oldest first and
    /// right-aligned in an array of length n (NaN in front while history is still shorter than that).
    /// </summary>
    public double[] Recent(int n, DateTime nowUtc, Func<Totals, double> pick)
    {
        var res = new double[n];
        Array.Fill(res, double.NaN);
        lock (_lock)
        {
            int a = Math.Max(LowerBound(nowUtc.Ticks - (long)n * TimeSpan.TicksPerSecond), _count - n);
            int len = _count - a;
            for (int k = 0; k < len; k++) res[n - len + k] = pick(_tot[Phys(a + k)]);
        }
        return res;
    }
}
