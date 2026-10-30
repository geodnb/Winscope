using System.Globalization;
using System.Text;
using WinScope.Core;

namespace WinScope.Modules.Events;

/// <summary>Everything the report needs, taken from data the module already holds (no I/O).</summary>
public sealed class EventsReportInput
{
    public IReadOnlyList<EventGroup> Groups { get; init; } = Array.Empty<EventGroup>();
    public bool Loaded { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>Events older than this weren't loaded because a log hit its entry cap (null = complete).</summary>
    public DateTime? CompleteSinceUtc { get; init; }
    /// <summary>The range chosen in the Events view (1, 7 or 30 days).</summary>
    public int RangeDays { get; init; } = 7;
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
    /// <summary>Time since Windows last fully started (tick count).</summary>
    public TimeSpan Uptime { get; init; }
    public DateTime? LastStartUtc { get; init; }
    public int LastStartKind { get; init; } = -1;
    public bool Redact { get; init; } = true;
    /// <summary>
    /// Display title for a group (e.g. "Other errors" use the message's first line once loaded). Null = rule title.
    /// Only used when not redacting: message text can hold e-mail addresses, file and server names.
    /// </summary>
    public Func<EventGroup, string>? TitleOf { get; init; }
}

/// <summary>
/// Builds the Events part of a WinScope report: plain text for someone who can't see the screen. Pure and fast
/// (a few passes over the already-aggregated groups); never throws to the caller.
/// </summary>
public static class EventsReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private const int MaxProblems = 25, MaxFindings = 5, MaxLine = 108, MaxOtherSources = 5;

    /// <summary>Order in which kinds become key findings (most serious first).</summary>
    private static readonly string[] FindingOrder =
    {
        Kinds.Bsod, Kinds.Unexpected, Kinds.HwFatal, Kinds.Disk, Kinds.Tdr, Kinds.AppCrash, Kinds.AppHang, Kinds.LowMemory,
        Kinds.WifiDrop, Kinds.WifiAdapter, Kinds.EthDrop, Kinds.NetAdapter, Kinds.GpuError, Kinds.HwCorrected,
        Kinds.UpdateFail, Kinds.Service, Kinds.Driver, Kinds.DumpFail, Kinds.WifiFail,
    };

    public static ReportSection Build(EventsReportInput input)
    {
        try { return BuildCore(input); }
        catch (Exception ex)
        {
            return new ReportSection { Title = "Events", Body = "(The events summary couldn't be produced: " + ex.Message + ")" };
        }
    }

    private static ReportSection BuildCore(EventsReportInput i)
    {
        if (!i.Loaded)
            return new ReportSection { Title = "Events", Body = "The event logs are still being read – this part isn't available yet. Try again in a few seconds." };

        var now = i.NowUtc;
        var nowLocal = now.ToLocalTime();
        int longDays = Math.Max(7, i.RangeDays is 1 or 7 or 30 ? i.RangeDays : 7);
        var since24 = now - TimeSpan.FromHours(24);
        var sinceLong = now - TimeSpan.FromDays(longDays);
        string longLabel = $"last {longDays} days", label24 = "last 24 hours";
        // If a log hit its entry cap, the data only reaches back to CompleteSinceUtc: say so in the labels themselves.
        bool capped = i.CompleteSinceUtc is DateTime cs && cs > sinceLong;
        if (capped)
        {
            var cs2 = i.CompleteSinceUtc!.Value;
            sinceLong = cs2;
            longLabel = "period since " + LongWhen(cs2.ToLocalTime(), nowLocal);
            if (cs2 > since24) { since24 = cs2; label24 = longLabel; }
        }
        var sb = new StringBuilder();

        // ------------------------------------------------------------ header
        sb.Append("Read from the Windows System and Application event logs (newest 5,000 entries per log, at most 30 days).\n");
        if (capped)
            sb.Append($"Covers {LongWhen(sinceLong.ToLocalTime(), nowLocal)} – now (the {longDays} days asked for weren't all loaded: older events\n")
              .Append("are beyond the 5,000-entry cap), and separately the last 24 hours.\n");
        else
        {
            sb.Append($"Covers the {longLabel} and, separately, the last 24 hours");
            if (i.RangeDays == 1) sb.Append(" (the Events view is set to 24 hours;\n7 days are included for context)");
            sb.Append(".\n");
        }
        sb.Append("Repeats of the same event within 10 minutes are counted together and shown as \"×N\". Duplicate records of one\n")
          .Append("incident (for example a blue screen logged by three different parts of Windows) are counted once.\n");
        sb.Append(BootText(now.ToLocalTime(), i.Uptime, i.LastStartUtc?.ToLocalTime(), i.LastStartKind)).Append('\n');

        // ------------------------------------------------------------ summary counts
        var k24 = EventAggregator.CountKinds(i.Groups, since24, EvSeverity.Problem);
        var kLong = EventAggregator.CountKinds(i.Groups, sinceLong, EvSeverity.Problem);
        int minor24 = CountSeverity(i.Groups, since24, EvSeverity.Minor), minorLong = CountSeverity(i.Groups, sinceLong, EvSeverity.Minor);
        sb.Append("\nProblems by type (occurrences):\n");
        Wrap(sb, $"- {Cap(label24)}: " + (k24.Count == 0 ? "none" : EventAggregator.Sentence(k24, 12)) + OtherNote(minor24));
        Wrap(sb, $"- {Cap(longLabel)}: " + (kLong.Count == 0 ? "none" : EventAggregator.Sentence(kLong, 12)) + OtherNote(minorLong));

        // ------------------------------------------------------------ by category
        var cats = new Dictionary<EvCategory, (int all, int critical)>();
        foreach (var g in i.Groups)
            foreach (var c in g.Items)
            {
                if (c.TimeUtc < sinceLong || c.Harmless || c.Severity < EvSeverity.Minor) continue;
                cats.TryGetValue(c.Category, out var v);
                cats[c.Category] = (v.all + 1, v.critical + (c.Severity == EvSeverity.Critical ? 1 : 0));
            }
        if (cats.Count > 0)
        {
            sb.Append($"\nBy category ({longLabel}, problems and errors, occurrences):\n");
            foreach (var cat in Enum.GetValues<EvCategory>())
                if (cats.TryGetValue(cat, out var v))
                    sb.Append("- ").Append(EvText.CategoryName(cat)).Append(": ").Append(v.all.ToString(Inv))
                      .Append(v.critical > 0 ? $" ({v.critical.ToString(Inv)} serious)" : "").Append('\n');
        }

        // ------------------------------------------------------------ problems list
        var problems = i.Groups.Where(g => g.Newest.TimeUtc >= sinceLong && EvText.IsProblem(g.Newest)).ToList();   // newest first
        sb.Append($"\nProblems, newest first ({longLabel}");
        sb.Append(problems.Count > MaxProblems ? $", newest {MaxProblems} of {problems.Count}" : "").Append("):\n");
        if (problems.Count == 0) sb.Append("- None.\n");
        foreach (var g in problems.Take(MaxProblems))
        {
            var c = g.Newest;
            string title = ItemTitle(i, g, out string explain);           // already redacted: safe to shorten
            string head = $"- {When(c.TimeUtc.ToLocalTime(), nowLocal)}{(g.Count > 1 ? " ×" + g.Count.ToString(Inv) : "")}" +
                          $"{(c.Severity == EvSeverity.Critical ? " [serious]" : "")} {title}";
            sb.Append(Shorten(head, MaxLine)).Append('\n');
            string first = Sentences(explain, MaxLine - 2);
            if (first.Length > 0) sb.Append("  ").Append(Shorten(first, MaxLine - 2)).Append('\n');
        }
        if (problems.Count > MaxProblems) sb.Append($"- and {problems.Count - MaxProblems} more\n");

        // ------------------------------------------------------------ other errors (unexplained)
        var other = i.Groups.SelectMany(g => g.Items).Where(c => c.TimeUtc >= sinceLong && !c.Harmless && c.Severity == EvSeverity.Minor).ToList();
        if (other.Count > 0)
        {
            // Per source: occurrences, plus the latest item's title (the message's first line once it has been loaded).
            var bySource = i.Groups.Where(g => g.Newest.TimeUtc >= sinceLong && !g.Newest.Harmless && g.Newest.Severity == EvSeverity.Minor)
                .GroupBy(g => g.Newest.Ev.Provider + " event " + g.Newest.Ev.Id.ToString(Inv))
                .Select(x => (Key: x.Key, n: x.Sum(g => g.Items.Count(c => c.TimeUtc >= sinceLong)), latest: ItemTitle(i, x.First(), out _)))
                .OrderByDescending(x => x.n).ThenBy(x => x.Key, StringComparer.Ordinal).ToList();
            sb.Append('\n');
            Wrap(sb, $"Other errors and minor warnings ({longLabel}): {other.Count.ToString(Inv)} – logged by Windows or apps without a " +
                     "known explanation; usually harmless unless they line up with a problem. Most frequent:", indent: "");
            foreach (var (key, n, latest) in bySource.Take(MaxOtherSources))
            {
                sb.Append(Shorten($"- {key} ×{n.ToString(Inv)}", MaxLine)).Append('\n');
                // Redacting: message text is never printed, so a "latest" line would only repeat the source.
                // Otherwise skip the rule's placeholder ("<provider> reported an error …"), which adds nothing.
                string provider = key[..key.LastIndexOf(" event ", StringComparison.Ordinal)];
                if (!i.Redact && !latest.StartsWith(provider + " reported", StringComparison.Ordinal))
                    sb.Append("  ").Append(Shorten("latest: " + latest, MaxLine - 2)).Append('\n');
            }
            if (bySource.Count > MaxOtherSources) sb.Append($"- and {bySource.Count - MaxOtherSources} more sources\n");
        }

        // ------------------------------------------------------------ hidden / missing
        int harmless = i.Groups.SelectMany(g => g.Items).Count(c => c.TimeUtc >= sinceLong && c.Harmless);
        int info = i.Groups.SelectMany(g => g.Items).Count(c => c.TimeUtc >= sinceLong && !c.Harmless && c.Severity == EvSeverity.Info);
        sb.Append($"\nNot listed: {Plural(harmless, "known-harmless warning")} (DCOM permission warnings, time-sync retries and similar,\n")
          .Append($"which Microsoft says are safe to ignore) and {Plural(info, "information event")} (starts, sleep and wake, installed\n")
          .Append("updates).\n");
        foreach (var n in i.Notes) sb.Append("Note: ").Append(n).Append('\n');

        // ------------------------------------------------------------ findings
        var findings = Findings(i, kLong, sinceLong, longLabel);

        string body = sb.ToString();
        if (i.Redact)
        {
            body = Redactor.Apply(body);
            findings = findings.Select(Redactor.Apply).ToList();
        }
        return new ReportSection { Title = "Events", Findings = findings, Body = body };
    }

    private static string OtherNote(int minor) => minor > 0 ? $"; plus {Plural(minor, "minor error")} without a known explanation" : "";

    /// <summary>Append text wrapped at word boundaries to lines of at most MaxLine characters (continuations indented).</summary>
    private static void Wrap(StringBuilder sb, string text, string indent = "  ")
    {
        var line = new StringBuilder();
        bool start = true;                                   // no word on this line yet (indent doesn't count)
        foreach (var word in text.Split(' '))
        {
            if (!start && line.Length + 1 + word.Length > MaxLine)
            {
                sb.Append(line).Append('\n');
                line.Clear().Append(indent);
                start = true;
            }
            if (!start) line.Append(' ');
            line.Append(word);
            start = false;
        }
        if (line.Length > 0) sb.Append(Shorten(line.ToString(), MaxLine)).Append('\n');
    }

    private static string Plural(int n, string one) => n.ToString(Inv) + " " + (n == 1 ? one : one + "s");

    private static int CountSeverity(IEnumerable<EventGroup> groups, DateTime since, EvSeverity sev) =>
        groups.SelectMany(g => g.Items).Count(c => c.TimeUtc >= since && !c.Harmless && c.Severity == sev);

    private static List<string> Findings(EventsReportInput i, List<(string Kind, int Count)> counts, DateTime since, string label)
    {
        var result = new List<string>();
        var now = i.NowUtc.ToLocalTime();
        foreach (var kind in FindingOrder)
        {
            if (result.Count >= MaxFindings) break;
            int n = counts.FirstOrDefault(k => k.Kind == kind).Count;
            if (n == 0) continue;
            // Small things only become a key finding when they repeat.
            if (kind is Kinds.HwCorrected or Kinds.Service or Kinds.Driver or Kinds.WifiFail or Kinds.GpuError && n < 3) continue;
            var groups = i.Groups.Where(g => g.Items.Any(c => c.Kind == kind && c.TimeUtc >= since && EvText.IsProblem(c))).ToList();
            if (groups.Count == 0) continue;
            var latest = groups[0];                                            // groups are newest first
            string title = ItemTitle(i, latest, out _);
            string line = $"{Kinds.Phrase(kind, n)} in the {label}";
            if (kind is Kinds.AppCrash or Kinds.AppHang)
            {
                var top = groups.SelectMany(g => g.Items).Where(c => c.Kind == kind && c.TimeUtc >= since && c.Subject != null)
                                .GroupBy(c => c.Subject!, StringComparer.OrdinalIgnoreCase).Select(x => (app: x.Key, n: x.Count()))
                                .OrderByDescending(x => x.n).Take(2).ToList();
                if (top.Count > 0) line += ", mostly " + string.Join(" and ", top.Select(t => $"{Safe(i, t.app)} ({t.n.ToString(Inv)})"));
                line += $" – latest {When(latest.Newest.TimeUtc.ToLocalTime(), now)}";
            }
            else line += $" – latest {When(latest.Newest.TimeUtc.ToLocalTime(), now)}: {title}";
            result.Add(Shorten(line, MaxLine + 20));
        }
        if (result.Count == 0)
            result.Add(counts.Count == 0 ? $"No problems logged in the {label}" : $"Only minor problems logged in the {label}");
        return result;
    }

    /// <summary>
    /// Title and explanation for an item, ready to print. When redacting: no formatted message text at all (an
    /// unexplained error becomes "Provider – event ID N (Error)"), the Wi-Fi network name swapped for a stand-in,
    /// device instance ids cut down to bus + vendor/product ids, and the shared redaction applied – all before any
    /// shortening, so a cut can't hide a value from the patterns.
    /// </summary>
    private static string ItemTitle(EventsReportInput i, EventGroup g, out string explain)
    {
        var c = g.Newest;
        if (!i.Redact)
        {
            explain = c.Explain;
            return i.TitleOf?.Invoke(g) ?? c.Title;
        }
        string title = c.MessageIsTitle
            ? $"{c.Ev.Provider} – event ID {c.Ev.Id.ToString(Inv)} ({c.Ev.LevelName})"
            : c.Title;
        explain = c.Explain;
        // The stand-in goes into the SSID slot itself (re-rendered by the rule), never by search-and-replace over the
        // finished text – an SSID like "Wi-Fi" or "PC" would otherwise rewrite ordinary words. Calling NetworkName
        // also registers the SSID, so the shell's word-bounded final pass catches any other mention.
        if (c.WithNetworkName != null && !string.IsNullOrWhiteSpace(c.Subject))
            (title, explain) = c.WithNetworkName(Redactor.NetworkName(c.Subject));
        explain = Safe(i, explain);
        return Safe(i, title).Replace('\n', ' ');
    }

    /// <summary>Redact one raw value (device ids, account SIDs, then the shared redaction) when redacting.</summary>
    private static string Safe(EventsReportInput i, string s) => i.Redact ? Redactor.Apply(DeviceIds.Scrub(s)) : s;

    /// <summary>"Tue 21 Oct 09:12".</summary>
    private static string LongWhen(DateTime local, DateTime nowLocal) =>
        local.Date == nowLocal.Date ? "today " + local.ToString("HH:mm", Inv) : local.ToString("ddd d MMM HH:mm", Inv);

    /// <summary>As many whole sentences as fit in <paramref name="max"/> characters (at least the first, shortened).</summary>
    private static string Sentences(string s, int max)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Replace('\n', ' ').Trim();
        int end = -1;
        for (int k = 0; k < s.Length; k++)
            if (s[k] == '.' && (k == s.Length - 1 || s[k + 1] == ' ') && k > 15)
            {
                if (k + 1 > max) break;
                end = k + 1;
            }
        return end > 0 ? s[..end] : Shorten(s, max);
    }

    private static string Cap(string s) => s.Length == 0 ? s : char.ToUpperInvariant(s[0]) + s[1..];

    public static string Shorten(string s, int max) => s.Length <= max ? s : s[..(max - 1)].TrimEnd() + "…";

    /// <summary>"14:05" today, "Tue 14:05" this week, "12 Oct 14:05" older.</summary>
    public static string When(DateTime local, DateTime nowLocal)
    {
        if (local.Date == nowLocal.Date) return local.ToString("HH:mm", Inv);
        return (nowLocal - local).TotalDays < 6 ? local.ToString("ddd HH:mm", Inv) : local.ToString("d MMM HH:mm", Inv);
    }

    /// <summary>
    /// "Last restart: Tue 08:12 (up 2d 4h)" or, after a Fast Startup boot, "Last start: Wed 07:55 (Fast Startup –
    /// Windows wasn't fully restarted); last full restart: Sun 19:02". The tick count only resets on a full start.
    /// </summary>
    public static string BootText(DateTime nowLocal, TimeSpan uptime, DateTime? lastStartLocal, int lastStartKind, bool compact = false)
    {
        var fullBoot = nowLocal - uptime;
        string sep = compact ? "  ·  " : "; ";
        if (lastStartLocal is DateTime start && lastStartKind is 1 or 2 && start > fullBoot + TimeSpan.FromMinutes(3) && start <= nowLocal)
        {
            string how = lastStartKind == 1 ? (compact ? "Fast Startup" : "Fast Startup – Windows wasn't fully restarted") : "resumed from hibernation";
            return $"Last start: {WhenDay(start, nowLocal)} ({how}){sep}{(compact ? "Last" : "last")} full restart: {WhenDay(fullBoot, nowLocal)}";
        }
        return compact
            ? $"Last restart: {WhenDay(fullBoot, nowLocal)}  ·  Up {UI.Format.Span(uptime)}"
            : $"Last restart: {WhenDay(fullBoot, nowLocal)} (up {UI.Format.Span(uptime)})";
    }

    private static string WhenDay(DateTime local, DateTime nowLocal) =>
        local.Date == nowLocal.Date ? "today " + local.ToString("HH:mm", Inv) : When(local, nowLocal);
}
