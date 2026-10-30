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
    /// <summary>The reported graphics adapter's name and video memory size, if Windows told us (null / 0 otherwise).</summary>
    public string? GpuName;
    public long GpuVramTotal;
    /// <summary>Dedicated video memory in use on that adapter (bytes, NaN if unknown).</summary>
    public double GpuVramUsed = double.NaN;
    /// <summary>Graphics adapters seen in the GPU counters (2 = e.g. RTX card + integrated graphics).</summary>
    public int GpuAdapterCount;
    /// <summary>False on a non-64-bit build: per-app, GPU and disk figures are then unavailable.</summary>
    public bool Supported = true;
}
