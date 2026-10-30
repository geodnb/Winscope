using System.Runtime.InteropServices;
using static WinScope.Modules.Performance.Native;

namespace WinScope.Modules.Performance;

/// <summary>The latest values of one (possibly wildcard) counter, copied out of native memory.</summary>
internal sealed class PdhArray
{
    public byte[] Data = new byte[16 * 1024];
    public int Length;
    public readonly List<PdhItem> Items = new();
    public bool Ok;
    public ReadOnlySpan<byte> Span => Data.AsSpan(0, Length);
}

/// <summary>
/// Minimal PDH (Performance Data Helper) query wrapper. Counters are added by their English paths so it works on
/// any display language. One instance is used only from the sampler loop (never concurrently); Dispose closes the
/// query and frees the native buffer and must be called from that same loop (it is, in its finally block).
/// </summary>
internal sealed class PdhQuery : IDisposable
{
    private IntPtr _query;
    private IntPtr _native;
    private int _nativeSize;
    private const int MaxBuffer = 32 * 1024 * 1024;
    private const uint Format = PDH_FMT_DOUBLE | PDH_FMT_NOCAP100;

    private PdhQuery(IntPtr q) { _query = q; }

    public static PdhQuery? TryOpen()
    {
        try
        {
            return PdhOpenQueryW(null, IntPtr.Zero, out var q) == ERROR_SUCCESS && q != IntPtr.Zero ? new PdhQuery(q) : null;
        }
        catch (DllNotFoundException) { return null; }
        catch (EntryPointNotFoundException) { return null; }
    }

    /// <summary>Add a counter; returns IntPtr.Zero if this PC doesn't have it (e.g. no WDDM 2 GPU counters).</summary>
    public IntPtr Add(string englishPath)
    {
        if (_query == IntPtr.Zero) return IntPtr.Zero;
        return PdhAddEnglishCounterW(_query, englishPath, IntPtr.Zero, out var c) == ERROR_SUCCESS ? c : IntPtr.Zero;
    }

    /// <summary>Take a reading. PDH_NO_DATA (e.g. a wildcard with no instances right now) isn't treated as a failure.</summary>
    public bool Collect()
    {
        if (_query == IntPtr.Zero) return false;
        uint rc = PdhCollectQueryData(_query);
        return rc == ERROR_SUCCESS || rc == PDH_NO_DATA;
    }

    /// <summary>
    /// Read all instances of <paramref name="counter"/> into <paramref name="dest"/>. Handles PDH_MORE_DATA (the
    /// instance list can grow between the sizing call and the read) by growing the buffer and retrying.
    /// </summary>
    public bool Read(IntPtr counter, PdhArray dest)
    {
        dest.Ok = false;
        dest.Items.Clear();
        dest.Length = 0;
        if (_query == IntPtr.Zero || counter == IntPtr.Zero) return false;
        if (_native == IntPtr.Zero) Grow(64 * 1024);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            uint size = (uint)_nativeSize;
            uint rc = PdhGetFormattedCounterArrayW(counter, Format, ref size, out uint count, _native);
            if (rc == PDH_MORE_DATA)
            {
                long want = Math.Max((long)size + 4096, (long)_nativeSize * 2);
                if (want > MaxBuffer) return false;
                Grow((int)want);
                continue;
            }
            if (rc != ERROR_SUCCESS) return false;          // PDH_INVALID_DATA on the first sample, PDH_NO_DATA if no instances
            int len = (int)Math.Min(size, (uint)_nativeSize);
            if (size == 0) len = _nativeSize;
            if (dest.Data.Length < len) dest.Data = new byte[Math.Max(len, dest.Data.Length * 2)];
            Marshal.Copy(_native, dest.Data, 0, len);
            dest.Length = len;
            PdhParser.ParseArray(dest.Span, (ulong)(long)_native, count, dest.Items);
            dest.Ok = true;
            return true;
        }
        return false;
    }

    private void Grow(int size)
    {
        if (_native != IntPtr.Zero) { Marshal.FreeHGlobal(_native); _native = IntPtr.Zero; _nativeSize = 0; }
        _native = Marshal.AllocHGlobal(size);
        _nativeSize = size;
    }

    public void Dispose()
    {
        try
        {
            if (_query != IntPtr.Zero) PdhCloseQuery(_query);   // also frees the counters added to it
        }
        finally
        {
            _query = IntPtr.Zero;
            if (_native != IntPtr.Zero) { Marshal.FreeHGlobal(_native); _native = IntPtr.Zero; _nativeSize = 0; }
        }
    }
}
