using System.Globalization;

namespace WinScope.Modules.Events;

// Pure data types shared by the reader, the rules, the aggregator and the views.
// Nothing in this file touches WPF or the Windows event log API, so it is unit-testable anywhere.

public enum EvCategory { Crashes, Graphics, Hardware, Network, Updates, Power, Services, Other }

/// <summary>
/// How worried the user should be. Minor = an error WinScope can't explain (the "Other errors" bucket) or a
/// low-importance driver notice: shown, but not counted as a "problem" on the dashboard.
/// </summary>
public enum EvSeverity { Info = 0, Minor = 1, Problem = 2, Critical = 3 }

/// <summary>One record copied out of the event log. Values are plain BCL types (string, numbers, Guid, DateTime).</summary>
public sealed class RawEvent
{
    public string Log { get; init; } = "";
    /// <summary>Bumped each time the log is cleared (record ids restart), so keys never collide across a clear.</summary>
    public int Generation { get; init; }
    public long RecordId { get; init; }
    public string Provider { get; init; } = "";
    public int Id { get; init; }
    public byte Level { get; init; }
    public DateTime TimeUtc { get; init; }
    /// <summary>EventRecord.Properties values in order (EventData/UserData leaves). Empty if the rule didn't need them.</summary>
    public object?[] Props { get; init; } = Array.Empty<object?>();
    /// <summary>Values fetched by name (EventData/Data[@Name=...]) for rules that asked for them; null if none.</summary>
    public IReadOnlyDictionary<string, object?>? Named { get; init; }

    private string? _key;
    /// <summary>Unique within this PC: "System#12345" (or "System#g2#12345" after the log was cleared twice).</summary>
    public string Key => _key ??= Log + "#" + (Generation == 0 ? "" : "g" + Generation.ToString(CultureInfo.InvariantCulture) + "#")
                                  + RecordId.ToString(CultureInfo.InvariantCulture);

    public object? Raw(int i) => i >= 0 && i < Props.Length ? Props[i] : null;

    /// <summary>Named value if present and non-empty, else the positional fallback (if any).</summary>
    public object? Field(string name, int fallback = -1)
    {
        if (Named != null && Named.TryGetValue(name, out var v) && v != null && !(v is string s && string.IsNullOrWhiteSpace(s)))
            return v;
        return fallback >= 0 ? Raw(fallback) : null;
    }

    public string? Text(string name, int fallback = -1) => Val.Str(Field(name, fallback));
    public string? TextAt(int i) => Val.Str(Raw(i));

    public string LevelName => Level switch
    {
        1 => "Critical",
        2 => "Error",
        3 => "Warning",
        4 => "Information",
        5 => "Verbose",
        0 => "Information",
        _ => "Level " + Level.ToString(CultureInfo.InvariantCulture)
    };
}

/// <summary>A raw event after the curated rules have explained it. Immutable; context passes make copies with "with".</summary>
public sealed record Classified
{
    public required RawEvent Ev { get; init; }
    /// <summary>Short rule id used by the duplicate-folding pass, e.g. "kp41", "bsod", "tdr".</summary>
    public string Rule { get; init; } = "";
    public EvCategory Category { get; init; }
    public EvSeverity Severity { get; init; }
    public bool Harmless { get; init; }
    public string Title { get; init; } = "";
    public string Explain { get; init; } = "";
    public IReadOnlyList<string> Tips { get; init; } = Array.Empty<string>();
    /// <summary>Counting bucket for summary sentences (see <see cref="Kinds"/>); null = not counted.</summary>
    public string? Kind { get; init; }
    /// <summary>The title is only a placeholder – show the first line of the formatted message instead (loaded lazily).</summary>
    public bool MessageIsTitle { get; init; }
    /// <summary>Show a "Windows says: …" line from the formatted message (loaded lazily).</summary>
    public bool ShowMessageLine { get; init; }
    /// <summary>Which lines of the message to pick for the "Windows says" line ("whea" or null = first line).</summary>
    public string? MessageHint { get; init; }
    /// <summary>App name / update title etc. used to match duplicates across providers.</summary>
    public string? Subject { get; init; }
    /// <summary>Stop code or similar numeric detail.</summary>
    public ulong Code { get; init; }
    /// <summary>A plain network disconnect – downgraded to Info when it lines up with sleep or shutdown.</summary>
    public bool NetDrop { get; init; }
    /// <summary>
    /// For events that name a Wi-Fi network (<see cref="Subject"/>): renders the title and explanation with a different
    /// network name in the SSID slot – used by reports to put in a stand-in without search-and-replace over the text.
    /// </summary>
    public Func<string, (string Title, string Explain)>? WithNetworkName { get; init; }
    /// <summary>Windows Error Reporting report id (GUID), used to drop re-sent copies of the same report.</summary>
    public string? ReportId { get; init; }
    /// <summary>Raise to Critical when the same thing (Rule + Subject) happens 3+ times within 24 h.</summary>
    public bool RepeatCritical { get; init; }

    public DateTime TimeUtc => Ev.TimeUtc;

    /// <summary>Identical repeats share this key.</summary>
    public string GroupKey => Ev.Provider + "|" + Ev.Id.ToString(CultureInfo.InvariantCulture) + "|" + (int)Severity + "|" + (Harmless ? "h" : "") + "|" + Title;

    // Records compare by value by default; occurrences are distinct events, so compare by reference.
    public bool Equals(Classified? other) => ReferenceEquals(this, other);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>One line in the timeline: identical repeats within 10 minutes collapsed together ("×N").</summary>
public sealed class EventGroup
{
    /// <summary>Stable across refreshes: the key of the oldest occurrence.</summary>
    public required string Identity { get; init; }
    /// <summary>Occurrences, newest first. Never empty.</summary>
    public required IReadOnlyList<Classified> Items { get; init; }
    /// <summary>Duplicate events from other sources folded into this one (e.g. Kernel-Power 41 behind a blue screen).</summary>
    public IReadOnlyList<Classified> Related { get; init; } = Array.Empty<Classified>();
    public Classified Newest => Items[0];
    public Classified Oldest => Items[Items.Count - 1];
    public int Count => Items.Count;
}

/// <summary>Display names and the dashboard's notion of a "problem" (shared by views and reports).</summary>
public static class EvText
{
    public static string CategoryName(EvCategory c) => c switch
    {
        EvCategory.Crashes => "Crashes",
        EvCategory.Graphics => "Graphics & drivers",
        EvCategory.Hardware => "Hardware",
        EvCategory.Network => "Network",
        EvCategory.Updates => "Updates",
        EvCategory.Power => "Power & startup",
        EvCategory.Services => "Services",
        _ => "Other",
    };

    /// <summary>Counts as a "problem": not harmless, and Problem or Critical severity.</summary>
    public static bool IsProblem(Classified c) => !c.Harmless && c.Severity >= EvSeverity.Problem;
}

/// <summary>Counting buckets for "1 blue screen, 3 app crashes, 2 Wi-Fi drops".</summary>
public static class Kinds
{
    public const string Bsod = "bsod", Unexpected = "unexpected", HwFatal = "hwfatal", HwCorrected = "hwcorrected",
        Disk = "disk", Tdr = "tdr", GpuError = "gpuerror", Driver = "driver", AppCrash = "appcrash", AppHang = "apphang",
        LowMemory = "lowmem", WifiDrop = "wifidrop", WifiFail = "wififail", WifiAdapter = "wifiadapter", EthDrop = "ethdrop",
        NetAdapter = "netadapter", UpdateFail = "updatefail", Service = "service", DumpFail = "dumpfail", Other = "other";

    public static readonly (string Key, string One, string Many)[] Order =
    {
        (Bsod, "blue screen", "blue screens"),
        (Unexpected, "unexpected shutdown", "unexpected shutdowns"),
        (HwFatal, "fatal hardware error", "fatal hardware errors"),
        (Disk, "disk problem", "disk problems"),
        (Tdr, "graphics driver reset", "graphics driver resets"),
        (GpuError, "graphics driver error", "graphics driver errors"),
        (AppCrash, "app crash", "app crashes"),
        (AppHang, "app freeze", "app freezes"),
        (LowMemory, "low-memory warning", "low-memory warnings"),
        (WifiDrop, "Wi-Fi drop", "Wi-Fi drops"),
        (WifiFail, "failed Wi-Fi connection", "failed Wi-Fi connections"),
        (WifiAdapter, "Wi-Fi adapter problem", "Wi-Fi adapter problems"),
        (EthDrop, "Ethernet drop", "Ethernet drops"),
        (NetAdapter, "network adapter reset", "network adapter resets"),
        (HwCorrected, "corrected hardware error", "corrected hardware errors"),
        (Driver, "driver problem", "driver problems"),
        (DumpFail, "failed crash dump", "failed crash dumps"),
        (UpdateFail, "failed update", "failed updates"),
        (Service, "service problem", "service problems"),
        (Other, "other error", "other errors"),
    };

    public static string Phrase(string key, int n)
    {
        foreach (var k in Order)
            if (k.Key == key) return n.ToString(CultureInfo.InvariantCulture) + " " + (n == 1 ? k.One : k.Many);
        return n.ToString(CultureInfo.InvariantCulture) + " " + key;
    }
}

/// <summary>Helpers for loosely-typed event property values.</summary>
public static class Val
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string? Str(object? v)
    {
        string? s = v switch
        {
            null => null,
            string x => x,
            byte[] => null,
            IFormattable f => f.ToString(null, Inv),
            _ => v.ToString()
        };
        if (s == null) return null;
        s = Clean(s);
        return s.Length == 0 ? null : s;
    }

    /// <summary>Trim, and drop the invisible direction marks Windows puts around dates and times (U+200E etc.).</summary>
    public static string Clean(string s)
    {
        bool dirty = false;
        foreach (char c in s)
            if (c is '‎' or '‏' or '\0' || (c >= '‪' && c <= '‮')) { dirty = true; break; }
        if (dirty)
        {
            var sb = new System.Text.StringBuilder(s.Length);
            foreach (char c in s)
                if (!(c is '‎' or '‏' or '\0' || (c >= '‪' && c <= '‮'))) sb.Append(c);
            s = sb.ToString();
        }
        return s.Trim();
    }

    /// <summary>Number from any integral type, or a string in decimal or 0x-hex.</summary>
    public static ulong? UInt(object? v)
    {
        switch (v)
        {
            case null: return null;
            case byte b: return b;
            case sbyte sb: return sb < 0 ? null : (ulong)sb;
            case short sh: return sh < 0 ? null : (ulong)sh;
            case ushort us: return us;
            case int i: return unchecked((uint)i);
            case uint ui: return ui;
            case long l: return unchecked((ulong)l);
            case ulong ul: return ul;
            case bool bo: return bo ? 1UL : 0UL;
            case string s:
                s = Clean(s);
                if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
                    return ulong.TryParse(s.AsSpan(2), NumberStyles.HexNumber, Inv, out var h) ? h : null;
                return ulong.TryParse(s, NumberStyles.Integer, Inv, out var d) ? d : null;
            default: return null;
        }
    }

    /// <summary>Like <see cref="UInt"/>, but a bare string is read as hex ("c0000005").</summary>
    public static ulong? Hex(object? v)
    {
        if (v is string s)
        {
            s = Clean(s);
            if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
            if (s.Length is 0 or > 16) return null;
            return ulong.TryParse(s, NumberStyles.HexNumber, Inv, out var h) ? h : null;
        }
        return UInt(v);
    }

    /// <summary>A string that reads like a name (has a letter) rather than a number or GUID-only value.</summary>
    public static string? Name(string? s) => s != null && s.Any(char.IsLetter) && !Guid.TryParse(s, out _) ? s : null;
}
