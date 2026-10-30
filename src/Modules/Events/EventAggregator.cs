using System.Globalization;
using System.Text.RegularExpressions;

namespace WinScope.Modules.Events;

/// <summary>
/// Turns classified occurrences into timeline groups:
///  1. context – plain network drops right next to sleep/shutdown/boot become Info (that's normal); disk errors
///     that keep repeating become Critical; the overclock/XMP tip is added to crashes only when there's evidence;
///  2. fold – the same incident logged by several providers becomes one item with the rest as "also logged"
///     (blue screen + Kernel-Power 41 + EventLog 6008 + WHEA, app crash + WER report, TDR + nvlddmkm 153, …);
///  3. collapse – identical repeats within 10 minutes (and the same day) become one item "×N".
/// Pure and deterministic; runs on the collection thread.
/// </summary>
public static class EventAggregator
{
    public static readonly TimeSpan RepeatWindow = TimeSpan.FromMinutes(10);

    public const string TipUnstable = "Several different programs have crashed recently, or Windows logged hardware errors – unstable CPU/GPU overclocks, undervolts or XMP/EXPO memory settings are a common cause, so test at stock settings.";

    public static List<EventGroup> Build(IEnumerable<Classified> input)
    {
        var list = input.OrderBy(c => c.TimeUtc).ThenBy(c => c.Ev.Log, StringComparer.Ordinal).ThenBy(c => c.Ev.RecordId).ToList();
        ApplyContext(list);
        var removed = new bool[list.Count];
        var related = Fold(list, removed);
        return Collapse(list, removed, related);
    }

    // ------------------------------------------------------------------ 1. context

    private static void ApplyContext(List<Classified> list)
    {
        var sleep = new List<DateTime>();
        var shutdown = new List<DateTime>();
        var boot = new List<DateTime>();
        var wake = new List<DateTime>();
        var hardware = new List<DateTime>();                    // blue screens and WHEA errors
        var crashes = new List<(DateTime T, string App)>();
        foreach (var c in list)
        {
            switch (c.Rule)
            {
                case "kp42": sleep.Add(c.TimeUtc); break;
                case "kg13":
                case "user32": shutdown.Add(c.TimeUtc); break;
                case "kg12":
                case "kb27": boot.Add(c.TimeUtc); break;
                case "kp107":
                case "pt-wake": wake.Add(c.TimeUtc); break;
                case "bsod":
                case "whea":
                case "whea-fatal":
                case "wer-bluescreen": hardware.Add(c.TimeUtc); break;
                case "kp41": if (c.Code != 0) hardware.Add(c.TimeUtc); break;
                case "app-crash": crashes.Add((c.TimeUtc, SubjectKey(c.Subject))); break;
            }
        }

        for (int i = 0; i < list.Count; i++)
        {
            var c = list[i];

            // Plain Wi-Fi / Ethernet disconnects around sleep, shutdown or start-up are normal. (Adapter or firmware
            // hangs such as Intel 5002/5007 are not plain drops and are never downgraded.)
            if (c.NetDrop && c.Rule is "wifi-drop" or "eth-down" && c.Severity != EvSeverity.Info)
            {
                var t = c.TimeUtc;
                string? why = null;
                if (Any(sleep, t, TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(90)) || Any(wake, t, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60)))
                    why = "while the PC was going to sleep or waking";
                else if (Any(shutdown, t, TimeSpan.FromSeconds(90), TimeSpan.FromMinutes(5)))
                    why = "while the PC was shutting down";
                else if (After(boot, t, TimeSpan.FromMinutes(3)))
                    why = "while the PC was starting up";
                if (why != null)
                {
                    const string normal = "This is normal: network adapters disconnect when the PC sleeps, shuts down or starts up. ";
                    string suffix = " (" + why + ")";
                    var render = c.WithNetworkName;
                    list[i] = c with
                    {
                        Severity = EvSeverity.Info,
                        Kind = null,
                        Title = c.Title + suffix,
                        Explain = normal + c.Explain,
                        Tips = Array.Empty<string>(),
                        WithNetworkName = render == null ? null : n => { var (t2, x2) = render(n); return (t2 + suffix, normal + x2); },
                    };
                    continue;
                }
            }

            // A drive error that keeps coming back (3+ for the same drive within 24 h) is serious even off the Windows drive.
            if (c.RepeatCritical && c.Severity == EvSeverity.Problem)
            {
                int n = 0;
                string key = SubjectKey(c.Subject);
                for (int j = i; j >= 0 && list[j].TimeUtc >= c.TimeUtc - TimeSpan.FromHours(24); j--)
                    if (list[j].Rule == c.Rule && SubjectKey(list[j].Subject) == key) n++;
                if (n >= 3)
                {
                    list[i] = c with
                    {
                        Severity = EvSeverity.Critical,
                        Explain = c.Explain + " It has happened several times in the last day, so it's worth acting on.",
                    };
                    continue;
                }
            }

            // Overclock / XMP advice only with evidence: 3+ different apps crashing within a week, or hardware errors.
            if (c.Rule == "app-crash" && c.Code is 0xC0000005 or 0xC000001D or 0xC0000409 or 0xC0000374)
            {
                var t = c.TimeUtc;
                var week = TimeSpan.FromDays(7);
                bool hw = hardware.Any(h => (h - t).Duration() <= week);
                int apps = crashes.Where(x => (x.T - t).Duration() <= week).Select(x => x.App).Distinct().Count();
                if (hw || apps >= 3)
                    list[i] = c with { Tips = new[] { TipUnstable }.Concat(c.Tips).Take(5).ToArray() };
            }
        }
    }

    /// <summary>Is there a marker within [t − before, t + after]?</summary>
    private static bool Any(List<DateTime> markers, DateTime t, TimeSpan before, TimeSpan after)
    {
        foreach (var m in markers)
            if (m >= t - before && m <= t + after) return true;
        return false;
    }

    /// <summary>Is t within <paramref name="within"/> after a marker?</summary>
    private static bool After(List<DateTime> markers, DateTime t, TimeSpan within)
    {
        foreach (var m in markers)
            if (t >= m && t - m <= within) return true;
        return false;
    }

    // ------------------------------------------------------------------ 2. fold duplicates

    private static Dictionary<int, List<Classified>> Fold(List<Classified> list, bool[] removed)
    {
        var related = new Dictionary<int, List<Classified>>();
        var byRule = new Dictionary<string, List<int>>();
        for (int i = 0; i < list.Count; i++)
        {
            if (!byRule.TryGetValue(list[i].Rule, out var l)) byRule[list[i].Rule] = l = new List<int>();
            l.Add(i);
        }

        IEnumerable<int> Of(string rule) => byRule.TryGetValue(rule, out var l) ? l : Enumerable.Empty<int>();

        void Absorb(int p, int ch)
        {
            removed[ch] = true;
            if (!related.TryGetValue(p, out var rl)) related[p] = rl = new List<Classified>();
            rl.Add(list[ch]);
            if (related.TryGetValue(ch, out var grand)) { rl.AddRange(grand); related.Remove(ch); }
        }

        // For each live parent, fold children whose time is within [parent − before, parent + after].
        // nearestOnly: just the closest matching child (one incident → one record from each source).
        void Fold(string parentRule, string childRule, TimeSpan before, TimeSpan after, bool nearestOnly, Func<Classified, Classified, bool>? match = null)
        {
            foreach (int p in Of(parentRule))
            {
                if (removed[p]) continue;
                var parent = list[p];
                int best = -1;
                TimeSpan bestGap = TimeSpan.MaxValue;
                foreach (int ch in Of(childRule))
                {
                    if (ch == p || removed[ch]) continue;
                    var child = list[ch];
                    if (child.TimeUtc < parent.TimeUtc - before || child.TimeUtc > parent.TimeUtc + after) continue;
                    if (match != null && !match(parent, child)) continue;
                    if (!nearestOnly) { Absorb(p, ch); continue; }
                    var gap = (child.TimeUtc - parent.TimeUtc).Duration();
                    if (gap < bestGap) { bestGap = gap; best = ch; }
                }
                if (best >= 0) Absorb(p, best);
            }
        }

        static bool SameSubject(Classified a, Classified b) => a.Subject != null && SubjectKey(a.Subject) == SubjectKey(b.Subject);
        static bool SameReport(Classified a, Classified b) => a.ReportId != null && a.ReportId == b.ReportId;
        // Same crash if the stop codes agree after normalising (0x1000007E = 0x7E); a Kernel-Power 41 that couldn't
        // record a code (0) on the same boot is the same crash too.
        static bool SameStop(Classified parent, Classified child) =>
            child.Code == 0 || parent.Code == 0 || EventRules.NormStop(child.Code) == EventRules.NormStop(parent.Code);
        static bool Is124(Classified p) => EventRules.NormStop(p.Code) == 0x124;

        var zero = TimeSpan.Zero;
        var m30 = TimeSpan.FromMinutes(30);
        var day = TimeSpan.FromHours(24);

        // Re-sent / re-queued Windows Error Reporting copies of the same report (same Report Id) → the first one.
        foreach (var r in new[] { "wer-app", "wer-lke", "wer-lke-gpu", "wer-bluescreen" })
            Fold(r, r, zero, TimeSpan.FromDays(31), false, SameReport);

        // Blue screen (WER-SystemErrorReporting 1001) is logged after boot; Kernel-Power 41 and EventLog 6008 come
        // earlier on the same boot – so only look back, take the nearest, and require matching stop codes.
        Fold("bsod", "kp41", m30, zero, true, SameStop);
        Fold("bsod", "6008", m30, zero, true);
        Fold("bsod", "wer-bluescreen", TimeSpan.FromMinutes(10), TimeSpan.FromHours(2), true);
        Fold("kp41", "6008", TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(15), true);
        Fold("kp41", "wer-bluescreen", TimeSpan.FromMinutes(10), TimeSpan.FromHours(2), true, (p, _) => p.Code != 0);
        // A 0x124 (WHEA_UNCORRECTABLE_ERROR) blue screen and the fatal WHEA record logged at the next boot are one incident.
        Fold("bsod", "whea-fatal", m30, m30, true, (p, _) => Is124(p));
        Fold("kp41", "whea-fatal", m30, m30, true, (p, _) => Is124(p));

        // App crash / freeze absorbs its WER report (which can be uploaded hours later) and the .NET Runtime record.
        Fold("app-crash", "wer-app", TimeSpan.FromMinutes(2), day, true, SameSubject);
        Fold("app-hang", "wer-app", TimeSpan.FromMinutes(2), day, true, SameSubject);
        Fold("app-crash", "dotnet-crash", TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(2), true, SameSubject);

        // Graphics driver reset: Display 4101 absorbs nvlddmkm 153 and the OpenGL timeout, plus the live dump that
        // WER may only report at the next boot.
        Fold("tdr", "nv-reset", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), false);
        Fold("tdr", "gl-tdr", TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60), false);
        Fold("tdr", "wer-lke-gpu", TimeSpan.FromMinutes(2), day, true);
        Fold("nv-reset", "wer-lke-gpu", TimeSpan.FromMinutes(2), day, true);

        // Starts and wakes.
        Fold("pt-wake", "kp107", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), true);
        Fold("kb27", "kg12", TimeSpan.FromMinutes(3), TimeSpan.FromMinutes(3), true);
        Fold("user32", "kg13", zero, TimeSpan.FromMinutes(10), true);

        // Update installed/failed absorbs its "started installing".
        Fold("wu-ok", "wu-start", TimeSpan.FromHours(6), zero, true, SameSubject);
        Fold("wu-fail", "wu-start", TimeSpan.FromHours(6), zero, true, SameSubject);
        return related;
    }

    public static string SubjectKey(string? s)
    {
        if (s == null) return "";
        s = s.Trim().ToLowerInvariant();
        return s.EndsWith(".exe", StringComparison.Ordinal) ? s[..^4] : s;
    }

    // ------------------------------------------------------------------ 3. collapse repeats

    private sealed class Builder
    {
        public readonly List<Classified> Items = new();
        public readonly List<Classified> Related = new();
        public DateTime OldestUtc;
        public DateTime OldestLocalDate;
        public string OldestKey = "";
    }

    private static List<EventGroup> Collapse(List<Classified> list, bool[] removed, Dictionary<int, List<Classified>> related)
    {
        var groups = new List<Builder>();
        var open = new Dictionary<string, Builder>(StringComparer.Ordinal);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            if (removed[i]) continue;
            var c = list[i];
            var localDate = c.TimeUtc.ToLocalTime().Date;
            string key = c.GroupKey;
            if (!open.TryGetValue(key, out var g) || g.OldestUtc - c.TimeUtc > RepeatWindow || g.OldestLocalDate != localDate)
            {
                g = new Builder();
                open[key] = g;
                groups.Add(g);
            }
            g.Items.Add(c);
            g.OldestUtc = c.TimeUtc;
            g.OldestLocalDate = localDate;
            g.OldestKey = c.Ev.Key;
            if (related.TryGetValue(i, out var rel)) g.Related.AddRange(rel);
        }
        var result = new List<EventGroup>(groups.Count);
        foreach (var g in groups)
            result.Add(new EventGroup
            {
                Identity = g.OldestKey,
                Items = g.Items.ToArray(),
                Related = g.Related.OrderBy(r => r.TimeUtc).ToArray(),
            });
        return result;
    }

    // ------------------------------------------------------------------ counting & sentences

    /// <summary>
    /// Occurrence counts per kind since a time, in display order. Counts every non-harmless occurrence at or above
    /// <paramref name="minSeverity"/> (Minor = everything except Info; Problem = what the dashboard calls problems).
    /// An item without a kind is counted as "other error" so totals always add up.
    /// </summary>
    public static List<(string Kind, int Count)> CountKinds(IEnumerable<EventGroup> groups, DateTime sinceUtc, EvSeverity minSeverity = EvSeverity.Minor)
    {
        if (minSeverity < EvSeverity.Minor) minSeverity = EvSeverity.Minor;
        var counts = new Dictionary<string, int>();
        foreach (var g in groups)
            foreach (var c0 in g.Items)
            {
                var c = c0;
                if (c.TimeUtc < sinceUtc || c.Harmless || c.Severity < minSeverity) continue;
                if (c.Kind == null) c = c with { Kind = Kinds.Other };
                string k = c.Kind!;
                counts[k] = counts.TryGetValue(k, out int n) ? n + 1 : 1;
            }
        var result = new List<(string, int)>();
        foreach (var k in Kinds.Order)
            if (counts.TryGetValue(k.Key, out int n)) result.Add((k.Key, n));
        return result;
    }

    /// <summary>"1 blue screen, 3 app crashes, 2 Wi-Fi drops" (empty string if none).</summary>
    public static string Sentence(IEnumerable<(string Kind, int Count)> counts, int max = 6)
    {
        var parts = counts.Select(k => Kinds.Phrase(k.Kind, k.Count)).ToList();
        if (parts.Count > max)
        {
            int rest = parts.Count - (max - 1);
            parts = parts.Take(max - 1).ToList();
            parts.Add($"{rest} other kinds");
        }
        return string.Join(", ", parts);
    }
}

/// <summary>Picking short lines out of formatted event messages.</summary>
public static class MessageText
{
    /// <summary>First non-empty line, shortened.</summary>
    public static string FirstLine(string? msg, int max = 160)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "";
        foreach (var raw in msg.Split('\n'))
        {
            var line = Val.Clean(raw.TrimEnd('\r'));
            if (line.Length == 0) continue;
            return Shorten(line, max);
        }
        return "";
    }

    public static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    private static readonly Dictionary<string, string> PciVendors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["10DE"] = "NVIDIA (likely the graphics card)",
        ["8086"] = "Intel (chipset, Wi-Fi, Ethernet or CPU)",
        ["1022"] = "AMD (chipset or CPU)",
        ["1002"] = "AMD/ATI graphics",
        ["144D"] = "Samsung (likely an NVMe SSD)",
        ["15B7"] = "Western Digital / SanDisk (likely an NVMe SSD)",
        ["1987"] = "Phison (likely an NVMe SSD controller)",
        ["1C5C"] = "SK hynix (likely an NVMe SSD)",
        ["2646"] = "Kingston (likely an NVMe SSD)",
        ["126F"] = "Silicon Motion (likely an NVMe SSD controller)",
        ["1E0F"] = "KIOXIA (likely an NVMe SSD)",
        ["1CC1"] = "ADATA (likely an NVMe SSD)",
        ["10EC"] = "Realtek (network or audio)",
        ["1B21"] = "ASMedia (USB or SATA controller)",
        ["14E4"] = "Broadcom",
        ["168C"] = "Qualcomm Atheros (Wi-Fi)",
        ["17CB"] = "Qualcomm (Wi-Fi)",
        ["14C3"] = "MediaTek (Wi-Fi)",
    };

    /// <summary>The part of the message worth showing on the item ("Windows says: …").</summary>
    public static string Summary(string? msg, string? hint)
    {
        if (string.IsNullOrWhiteSpace(msg)) return "";
        if (hint == "whea")
        {
            var picked = new List<string>();
            foreach (var raw in msg.Split('\n'))
            {
                var line = Val.Clean(raw.TrimEnd('\r'));
                if (line.StartsWith("Component:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Reported by component:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Error Source:", StringComparison.OrdinalIgnoreCase) ||
                    line.StartsWith("Error Type:", StringComparison.OrdinalIgnoreCase))
                    picked.Add(line);
            }
            var ven = Regex.Match(msg, @"VEN_([0-9A-Fa-f]{4})");
            if (ven.Success && PciVendors.TryGetValue(ven.Groups[1].Value, out var vendor))
                picked.Add("Device made by " + vendor);
            if (picked.Count > 0) return Shorten(string.Join(" · ", picked), 240);
        }
        return FirstLine(msg, 200);
    }
}

/// <summary>Which records a read pass covers: (AfterId, UpToId] within the last WindowMs. 0 = no bound.</summary>
public readonly record struct ReadBounds(long AfterId, long UpToId, long WindowMs);

/// <summary>Builds the event log queries (pure strings, unit-tested).</summary>
public static class EventQueries
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>Information-level events that matter, per log (the main select only takes levels 1–3).</summary>
    public static readonly Dictionary<string, (string Provider, int[] Ids)[]> InfoSelects = new()
    {
        ["System"] = new[]
        {
            ("Microsoft-Windows-Kernel-General", new[] { 12, 13 }),
            ("Microsoft-Windows-Kernel-Boot", new[] { 27 }),
            ("Microsoft-Windows-Kernel-Power", new[] { 42, 107 }),
            ("Microsoft-Windows-Power-Troubleshooter", new[] { 1 }),
            ("User32", new[] { 1074 }),
            ("Microsoft-Windows-WindowsUpdateClient", new[] { 19, 43 }),
            ("Microsoft-Windows-WLAN-AutoConfig", new[] { 8002, 8003 }),
        },
        ["Application"] = new[]
        {
            ("Windows Error Reporting", new[] { 1001 }),
        },
    };

    private static string Ids(int[]? ids) => ids == null || ids.Length == 0 ? "" :
        "(" + string.Join(" or ", ids.Select(i => "EventID=" + i.ToString(Inv))) + ") and ";

    /// <summary>
    /// Record-id window plus time window. The upper bound is the log's newest record id taken at the start of the
    /// pass (a high-water mark), so every query in the pass sees the same set of records and nothing written
    /// meanwhile can be skipped: it's picked up by the next pass.
    /// </summary>
    private static string Tail(ReadBounds b) =>
        (b.AfterId > 0 ? "EventRecordID > " + b.AfterId.ToString(Inv) + " and " : "") +
        (b.UpToId > 0 ? "EventRecordID <= " + b.UpToId.ToString(Inv) + " and " : "") +
        "TimeCreated[timediff(@SystemTime) <= " + Math.Max(1000, b.WindowMs).ToString(Inv) + "]";

    private const string Levels = "(Level=1 or Level=2 or Level=3) and ";

    // Application-log warnings never match a rule (they'd only be dropped), so only errors are read there –
    // that keeps chatty app warnings from using up the read cap.
    private static string LevelsFor(string log) => log == "Application" ? "(Level=1 or Level=2) and " : Levels;

    public static string LevelsXPath(string log, ReadBounds b) => "*[System[" + LevelsFor(log) + Tail(b) + "]]";

    public static string ProviderXPath(string provider, int[]? ids, bool levels, ReadBounds b) =>
        "*[System[Provider[@Name='" + provider + "'] and " + Ids(ids) + (levels ? Levels : "") + Tail(b) + "]]";

    /// <summary>Plain XPath queries (fallback if structured queries are rejected): levels first, then the info selects.</summary>
    public static List<string> MainPlain(string log, ReadBounds b)
    {
        var l = new List<string> { LevelsXPath(log, b) };
        if (InfoSelects.TryGetValue(log, out var sels))
            foreach (var (p, ids) in sels) l.Add(ProviderXPath(p, ids, false, b));
        return l;
    }

    private static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static string Structured(string log, IEnumerable<string> selects, IEnumerable<string> suppress)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("<QueryList><Query Id=\"0\" Path=\"").Append(log).Append("\">");
        foreach (var s in selects) sb.Append("<Select Path=\"").Append(log).Append("\">").Append(Esc(s)).Append("</Select>");
        foreach (var s in suppress) sb.Append("<Suppress Path=\"").Append(log).Append("\">").Append(Esc(s)).Append("</Suppress>");
        sb.Append("</Query></QueryList>");
        return sb.ToString();
    }

    private static IEnumerable<string> NoiseXPaths(bool levels, ReadBounds b) =>
        EventRules.NoiseSources.Select(n => levels ? ProviderXPath(n.Provider, n.Ids, true, b)
                                                   : "*[System[Provider[@Name='" + n.Provider + "']" + (n.Ids == null ? "" : " and " + Ids(n.Ids)[..^5]) + "]]");

    /// <summary>Levels + the info selects, with the chatty harmless providers suppressed.</summary>
    public static string MainStructured(string log, ReadBounds b) => Structured(log, MainPlain(log, b), NoiseXPaths(false, b));

    /// <summary>Only the chatty harmless providers (read with their own, smaller cap) – same bounds as the main query.</summary>
    public static string NoiseStructured(string log, ReadBounds b) => Structured(log, NoiseXPaths(true, b), Array.Empty<string>());
}
