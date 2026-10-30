using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WinScope.Modules.Performance;

/// <summary>One PDH counter instance value: name (UTF-16, inside the copied buffer) and its formatted double.</summary>
internal readonly struct PdhItem
{
    public readonly int NameOffset, NameChars;
    public readonly double Value;
    public PdhItem(int nameOffset, int nameChars, double value) { NameOffset = nameOffset; NameChars = nameChars; Value = value; }
    public ReadOnlySpan<char> Name(ReadOnlySpan<byte> buf) => MemoryMarshal.Cast<byte, char>(buf.Slice(NameOffset, NameChars * 2));
}

/// <summary>A graphics adapter, by its LUID (as it appears in the GPU counter instance names).</summary>
internal readonly record struct AdapterKey(uint LuidHigh, uint LuidLow);

/// <summary>Identifies one GPU engine: adapter LUID + physical adapter index + engine index.</summary>
internal readonly record struct EngineKey(uint LuidHigh, uint LuidLow, int Phys, int Eng)
{
    public AdapterKey Adapter => new(LuidHigh, LuidLow);
}

/// <summary>
/// Pure parsing for PdhGetFormattedCounterArrayW output and the GPU counter instance names.
///
/// PDH_FMT_COUNTERVALUE_ITEM_W (pdh.h), x64, 24 bytes:
///   0  LPWSTR szName                       – absolute pointer, into the same buffer (after the item array)
///   8  PDH_FMT_COUNTERVALUE FmtValue:
///        8   DWORD CStatus                 (+4 pad, the union is 8-byte aligned)
///        16  union { LONG; double; LONGLONG; LPCSTR; LPCWSTR }  – doubleValue with PDH_FMT_DOUBLE
/// </summary>
internal static class PdhParser
{
    public const int ItemSize = 24;
    private const int MaxItems = 200_000;
    private const int MaxNameChars = 1024;

    /// <summary>
    /// Read the items from a copy of the native buffer that started at <paramref name="baseAddress"/>. Items with a
    /// bad status (e.g. the first sample of a rate counter) or an out-of-bounds name are skipped.
    /// </summary>
    public static void ParseArray(ReadOnlySpan<byte> buf, ulong baseAddress, uint itemCount, List<PdhItem> output)
    {
        output.Clear();
        if (!ProcessParser.Supported) return;                               // layout above is the 64-bit one
        if (itemCount > MaxItems) itemCount = MaxItems;
        long need = (long)itemCount * ItemSize;
        if (need > buf.Length) itemCount = (uint)(buf.Length / ItemSize);
        for (int i = 0; i < itemCount; i++)
        {
            var it = buf.Slice(i * ItemSize, ItemSize);
            ulong namePtr = BinaryPrimitives.ReadUInt64LittleEndian(it);
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(it.Slice(8));
            double v = BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(it.Slice(16)));
            if (status != Native.PDH_CSTATUS_VALID_DATA && status != Native.PDH_CSTATUS_NEW_DATA) continue;
            if (double.IsNaN(v) || double.IsInfinity(v)) continue;
            if (namePtr < baseAddress) continue;
            ulong rel = namePtr - baseAddress;
            if (rel >= (ulong)buf.Length || (rel & 1) != 0) continue;
            int start = (int)rel;
            int maxChars = Math.Min(MaxNameChars, (buf.Length - start) / 2);
            var chars = MemoryMarshal.Cast<byte, char>(buf.Slice(start, maxChars * 2));
            int nul = chars.IndexOf('\0');
            if (nul < 0) continue;                                          // unterminated within bounds → junk
            output.Add(new PdhItem(start, nul, v));
        }
    }

    /// <summary>
    /// "pid_1234_luid_0x00000000_0x0000D1E5_phys_0_eng_3_engtype_3D" → pid 1234, engine (0, 0xD1E5, 0, 3).
    /// The engtype suffix (3D, Copy, VideoDecode, "High Priority 3D", …) is ignored: the engine index identifies it.
    /// </summary>
    public static bool TryParseGpuEngine(ReadOnlySpan<char> s, out int pid, out EngineKey key)
    {
        key = default;
        pid = 0;
        if (!Expect(ref s, "pid_") || !Dec(ref s, out pid)) return false;
        if (!Expect(ref s, "_luid_0x") || !Hex(ref s, out uint hi)) return false;
        if (!Expect(ref s, "_0x") || !Hex(ref s, out uint lo)) return false;
        if (!Expect(ref s, "_phys_") || !Dec(ref s, out int phys)) return false;
        if (!Expect(ref s, "_eng_") || !Dec(ref s, out int eng)) return false;
        key = new EngineKey(hi, lo, phys, eng);
        return true;
    }

    /// <summary>"pid_1234_luid_0x00000000_0x0000D1E5_phys_0" (GPU Process Memory instances) → 1234 + adapter.</summary>
    public static bool TryParseProcessMemory(ReadOnlySpan<char> s, out int pid, out AdapterKey adapter)
    {
        adapter = default;
        pid = 0;
        if (!Expect(ref s, "pid_") || !Dec(ref s, out pid)) return false;
        if (!Expect(ref s, "_")) return false;
        return TryParseAdapter(s, out adapter);
    }

    /// <summary>"luid_0x00000000_0x0000D1E5_phys_0" (GPU Adapter Memory instances) → adapter.</summary>
    public static bool TryParseAdapter(ReadOnlySpan<char> s, out AdapterKey adapter)
    {
        adapter = default;
        if (!Expect(ref s, "luid_0x") || !Hex(ref s, out uint hi)) return false;
        if (!Expect(ref s, "_0x") || !Hex(ref s, out uint lo)) return false;
        adapter = new AdapterKey(hi, lo);
        return true;
    }

    private static bool Expect(ref ReadOnlySpan<char> s, string token)
    {
        if (!s.StartsWith(token, StringComparison.OrdinalIgnoreCase)) return false;
        s = s[token.Length..];
        return true;
    }

    private static bool Dec(ref ReadOnlySpan<char> s, out int v)
    {
        v = 0;
        int i = 0;
        long acc = 0;
        while (i < s.Length && i < 10 && s[i] >= '0' && s[i] <= '9') { acc = acc * 10 + (s[i] - '0'); i++; }
        if (i == 0 || acc > int.MaxValue) return false;
        v = (int)acc;
        s = s[i..];
        return true;
    }

    private static bool Hex(ref ReadOnlySpan<char> s, out uint v)
    {
        v = 0;
        int i = 0;
        while (i < s.Length && i < 8)
        {
            char c = s[i];
            int d = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
            if (d < 0) break;
            v = (v << 4) | (uint)d;
            i++;
        }
        if (i == 0) return false;
        s = s[i..];
        return true;
    }
}

/// <summary>
/// Turns per-(process, engine) GPU utilisation into Task Manager-style figures for ONE adapter (the main/discrete
/// GPU, chosen by <see cref="AdapterPicker"/>), so an integrated GPU's video-decode engine can't masquerade as
/// "GPU 100 %" for the RTX card:
///  • adapter GPU % = max over that adapter's engines of (sum over processes on that engine)
///  • per-app GPU % = max over that adapter's engines of (sum over that app's processes on that engine)
///  • <c>other</c> = the same total for the busiest of the remaining adapters (NaN if there are none)
/// Reuses its dictionaries, so it allocates nothing per sample once warmed up.
/// </summary>
internal sealed class GpuAggregator
{
    private readonly Dictionary<EngineKey, double> _engine = new();
    private readonly Dictionary<(AppAgg app, EngineKey eng), double> _appEngine = new();

    /// <summary>
    /// Feed (pid, engine, value) triples; <paramref name="appForPid"/> maps a pid to its app (null = unknown/ended).
    /// <paramref name="primary"/> null = treat all adapters as one. Writes each app's <see cref="AppAgg.Gpu"/> (apps
    /// not seen keep the 0 they were reset to) and returns the primary adapter's total.
    /// </summary>
    public double Aggregate(IEnumerable<(int pid, EngineKey eng, double value)> items, Func<int, AppAgg?> appForPid,
        AdapterKey? primary, out double other)
    {
        _engine.Clear();
        _appEngine.Clear();
        bool anyOther = false;
        foreach (var (pid, eng, raw) in items)
        {
            bool mine = primary == null || eng.Adapter == primary.Value;
            if (!mine) anyOther = true;
            double v = Math.Clamp(raw, 0, 100);
            if (v <= 0) continue;
            _engine[eng] = _engine.GetValueOrDefault(eng) + v;
            if (!mine) continue;
            var app = pid > 0 ? appForPid(pid) : null;
            if (app != null) _appEngine[(app, eng)] = _appEngine.GetValueOrDefault((app, eng)) + v;
        }
        double total = 0, oth = 0;
        foreach (var (eng, v) in _engine)
        {
            if (primary == null || eng.Adapter == primary.Value) { if (v > total) total = v; }
            else if (v > oth) oth = v;
        }
        other = anyOther ? Math.Min(100, oth) : double.NaN;
        foreach (var ((app, _), v) in _appEngine) if (v > app.Gpu) app.Gpu = v;
        foreach (var ((app, _), _) in _appEngine) if (app.Gpu > 100) app.Gpu = 100;
        return Math.Min(100, total);
    }
}

/// <summary>
/// Chooses which graphics adapter to report: the one using the most dedicated video memory (a discrete card
/// like an RTX 4080 holds the desktop and games; an integrated GPU has only a small carve-out). Sticky, so it
/// doesn't flap: it only switches when another adapter has used clearly more (2× and +256 MB) for 10 picks in a row,
/// or the current one disappears. Without memory figures it falls back to the adapter with the most engines.
/// </summary>
internal sealed class AdapterPicker
{
    private AdapterKey? _current;
    private AdapterKey? _challenger;
    private int _streak;
    private const int SwitchAfter = 10;
    private const double Margin = 256.0 * 1024 * 1024;

    public AdapterKey? Current => _current;

    /// <param name="dedicated">Dedicated memory in use per adapter (bytes). May be empty.</param>
    /// <param name="engineCounts">Number of engines seen per adapter (fallback, and to know which adapters exist).</param>
    public AdapterKey? Pick(IReadOnlyDictionary<AdapterKey, double> dedicated, IReadOnlyDictionary<AdapterKey, int> engineCounts)
    {
        AdapterKey? best = null;
        double bestV = 0;
        foreach (var (a, v) in dedicated)
            if (v > bestV || (v == bestV && best != null && Less(a, best.Value))) { best = a; bestV = v; }
        if (best == null)
        {
            int bestN = 0;
            foreach (var (a, n) in engineCounts)
                if (n > bestN || (n == bestN && best != null && Less(a, best.Value))) { best = a; bestN = n; }
        }
        if (best == null) return _current;

        bool currentExists = _current is AdapterKey c && (dedicated.ContainsKey(c) || engineCounts.ContainsKey(c));
        if (!currentExists) { _current = best; _challenger = null; _streak = 0; return _current; }
        if (best.Value == _current!.Value) { _challenger = null; _streak = 0; return _current; }

        double cur = dedicated.GetValueOrDefault(_current.Value);
        bool clearlyMore = dedicated.Count > 0 ? bestV > cur * 2 + Margin : false;
        if (!clearlyMore) { _challenger = null; _streak = 0; return _current; }
        if (_challenger != best) { _challenger = best; _streak = 0; }
        if (++_streak >= SwitchAfter) { _current = best; _challenger = null; _streak = 0; }
        return _current;
    }

    private static bool Less(AdapterKey a, AdapterKey b) => a.LuidHigh != b.LuidHigh ? a.LuidHigh < b.LuidHigh : a.LuidLow < b.LuidLow;
}
