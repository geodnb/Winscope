using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using WinScope.Core;
using WinScope.UI;

namespace WinScope.Modules.Network;

/// <summary>
/// The Network tool's part of a WinScope report: plain text for someone who can't see the screen. Built only from
/// the latest immutable snapshot and the rate history (a short lock-protected copy), so it's quick and does no I/O.
/// Pure apart from the shared Redactor, so it's unit-tested with synthetic snapshots.
/// </summary>
internal static class NetworkReport
{
    public const string Title = "Network";
    private const int MaxApps = 10, MaxListenApps = 15, MaxPortsPerApp = 10, MaxRemotes = 8, MaxNameChars = 40;
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    public static ReportSection Build(NetSnapshot? s, RateHistory history, ReportOptions o, bool isAdmin)
    {
        if (s == null)
            return new ReportSection { Title = Title, Body = "Not available yet – WinScope has only just started measuring the network." };

        bool red = o.Redact;
        var findings = new List<string>();
        var b = new StringBuilder();
        var now = o.NowUtc;
        var samples = new List<RateSample>();
        history.CopySince(s.Utc.AddMinutes(-30), samples);

        // ---- what this covers
        var since = s.SessionStartUtc == default ? s.Utc : s.SessionStartUtc;
        b.Append($"Covers: since WinScope started at {When(since, now)} ({Format.Span(s.Utc - since)} ago)");
        b.Append(samples.Count > 1 ? $"; speed history for the last {Math.Max(1, (int)Math.Round((samples[^1].Utc - samples[0].Utc).TotalMinutes))} min.\n" : ".\n");
        b.Append("Speeds are totals across the adapters below, in bytes per second (KB/s, MB/s).\n");

        // ---- adapters
        b.Append("\nAdapters counted (virtual, VPN and loopback adapters are left out so nothing is counted twice):\n");
        if (s.Adapters.Count == 0)
        {
            b.Append("- None connected.\n");
            findings.Add("No connected network adapter (Wi-Fi or Ethernet) was found");
        }
        foreach (var a in s.Adapters.Take(8))
        {
            // The description is the driver's name (not user-editable); the alias is, and people often name it after
            // their Wi-Fi network – so when redacting only Windows' own default names are shown.
            string desc = a.Description.Length > 0 && !a.Description.Equals(a.Name, StringComparison.OrdinalIgnoreCase) ? $" ({Clean(a.Description, 50, red)})" : "";
            b.Append($"- {a.Kind}: {AdapterLabel(a, red)}{desc}{LinkText(a)}\n");
        }

        // ---- traffic
        b.Append("\nTraffic:\n");
        b.Append($"- Now: ↓ {Format.Rate(s.RxRate)} download, ↑ {Format.Rate(s.TxRate)} upload\n");
        if (samples.Count > 1)
        {
            double avgRx = samples.Average(x => x.Rx), avgTx = samples.Average(x => x.Tx);
            var pRx = samples.MaxBy(x => x.Rx); var pTx = samples.MaxBy(x => x.Tx);
            b.Append($"- Last {Minutes(samples)} min: average ↓ {Format.Rate(avgRx)} / ↑ {Format.Rate(avgTx)}; ");
            b.Append($"peak ↓ {Format.Rate(pRx.Rx)} at {When(pRx.Utc, now)}, peak ↑ {Format.Rate(pTx.Tx)} at {When(pTx.Utc, now)}\n");
        }
        b.Append($"- This session: {Format.Bytes(s.SessionRx)} downloaded, {Format.Bytes(s.SessionTx)} uploaded\n");

        // ---- per-app availability
        b.Append('\n');
        if (!isAdmin)
            b.Append("Per-app speeds: not available – WinScope isn't running as administrator, and Windows only gives " +
                     "per-app byte counts to admin tools. The connection lists below are complete without it.\n");
        else if (s.PerApp)
        {
            b.Append($"Per-app speeds: measured live (Windows network trace) since {When(s.PerAppSinceUtc ?? since, now)}. " +
                     "They count data only (not packet overhead) and leave out app-to-app traffic on this PC, so they add up to a bit less than the total.\n");
            if (s.EtwMessage != null) b.Append("Note: " + s.EtwMessage + "\n");
        }
        else
        {
            // The monitor's messages are whole sentences ("Per-app speeds aren't available: … Connections are still shown.").
            string why = s.EtwMessage ?? "Per-app speeds aren't available: the Windows network trace isn't running.";
            b.Append(why + "\n");
            findings.Add(why.Replace(" Connections are still shown.", "").TrimEnd('.', '…'));
        }

        // ---- top apps
        if (s.PerApp || s.Apps.Any(a => a.TotalDown + a.TotalUp > 0))
        {
            var byTraffic = s.Apps.Where(a => a.TotalDown + a.TotalUp > 0 || a.Down + a.Up >= 1)
                .OrderByDescending(a => a.TotalDown + a.TotalUp).ThenByDescending(a => a.Down + a.Up).ToList();
            b.Append("\nTop apps by traffic this session (downloaded / uploaded; speed now):\n");
            if (byTraffic.Count == 0) b.Append("- No app traffic measured yet.\n");
            foreach (var a in byTraffic.Take(MaxApps))
            {
                string nowTxt = a.Down + a.Up >= 1 ? $"; now ↓ {Format.Rate(a.Down)} ↑ {Format.Rate(a.Up)}" : "";
                b.Append($"- {AppName(a, red)}: {Format.Bytes(a.TotalDown)} / {Format.Bytes(a.TotalUp)}{nowTxt}\n");
            }
            More(b, byTraffic.Count - MaxApps, "app");
        }

        // ---- connections
        var all = s.Apps.SelectMany(a => a.Conns).ToList();
        var tcpRemote = all.Where(c => c.HasRemote).ToList();
        int udp = all.Count(c => c.Proto == Proto.Udp), listen = all.Count(c => c.IsListening);
        b.Append($"\nConnections now: {all.Count} in total ({tcpRemote.Count} TCP connections, {listen} TCP listening sockets, {udp} UDP endpoints)\n");
        if (s.ConnError != null)
        {
            b.Append($"- Note: part of the list couldn't be read ({s.ConnError}).\n");
            findings.Add($"Couldn't read the full connection list ({s.ConnError})");
        }
        if (tcpRemote.Count > 0)
        {
            int Count(Where w) => tcpRemote.Count(c => c.Where == w);
            var parts = new List<string>();
            foreach (var (w, label) in new[] { (Where.Internet, "Internet"), (Where.Lan, "LAN"), (Where.Shared, "Shared/VPN"), (Where.Local, "Local (this PC)") })
                if (Count(w) > 0) parts.Add($"{label} {Count(w)}");
            b.Append("- TCP connections, by where the other end is: " + string.Join(", ", parts) + "\n");
            b.Append("  (LAN = your local network; Shared/VPN = 100.64.x.x, used by carrier-grade NAT and VPNs such as Tailscale)\n");
            var st = tcpRemote.GroupBy(c => AddressClass.StateText(c)).OrderByDescending(g => g.Count()).Select(g => $"{g.Count()} {Lower(g.Key)}");
            b.Append("- TCP states: " + string.Join(", ", st) + "\n");
            int timeWait = tcpRemote.Count(c => c.State == TcpState.TimeWait);
            if (timeWait >= 3000)
                findings.Add($"{timeWait} TCP connections are in 'time wait' – something is opening and closing connections very fast");
        }

        var byConns = s.Apps.Where(a => a.Established > 0).OrderByDescending(a => a.Established).ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (byConns.Count > 0)
        {
            b.Append("\nApps with the most established connections:\n");
            foreach (var a in byConns.Take(MaxApps))
            {
                var w = a.Conns.Where(c => c.HasRemote && c.State == TcpState.Established).GroupBy(c => c.Where)
                    .OrderByDescending(g => g.Count()).Select(g => $"{AddressClass.Label(g.Key)} {g.Count()}");
                b.Append($"- {AppName(a, red)}: {a.Established} ({string.Join(", ", w)})\n");
            }
            More(b, byConns.Count - MaxApps, "app");
        }
        foreach (var a in s.Apps.Where(a => a.TcpAll >= 500).OrderByDescending(a => a.TcpAll).Take(2))
            findings.Add($"'{Clean(a.Name, MaxNameChars, red)}' has {a.TcpAll} TCP connections open, which is unusually many");

        // ---- busiest remote addresses
        RemoteAddresses(b, tcpRemote, s, o.Redact);

        // ---- listening ports
        ListeningPorts(b, s, red);

        // ---- busy-now / sustained-upload findings (after the structural ones, which matter more)
        if (s.RxRate >= 1024 * 1024 || s.TxRate >= 1024 * 1024)
        {
            string who = "";
            if (s.PerApp && s.Apps.OrderByDescending(a => a.Down + a.Up).FirstOrDefault() is { } top && top.Down + top.Up >= 64 * 1024)
                who = $", mostly '{Clean(top.Name, MaxNameChars, red)}' (↓ {Format.Rate(top.Down)}, ↑ {Format.Rate(top.Up)})";
            findings.Add($"Network busy right now: ↓ {Format.Rate(s.RxRate)} download, ↑ {Format.Rate(s.TxRate)} upload{who}");
        }
        var last10 = samples.Where(x => x.Utc >= s.Utc.AddMinutes(-10)).ToList();
        if (last10.Count >= 300 && last10.Average(x => x.Tx) >= 1024 * 1024)
        {
            string who = "";
            if (s.PerApp && s.Apps.OrderByDescending(a => a.Up).FirstOrDefault() is { } up && up.TotalUp > 0)
                who = $"; biggest uploader this session: '{Clean(up.Name, MaxNameChars, red)}' ({Format.Bytes(up.TotalUp)})";
            findings.Add($"Uploading steadily: average ↑ {Format.Rate(last10.Average(x => x.Tx))} over the last 10 min{who}");
        }

        string body = b.ToString();
        var f = findings.Take(5).ToList();
        if (o.Redact)
        {
            body = Redactor.Apply(body);
            f = f.Select(Redactor.Apply).ToList();
        }
        return new ReportSection { Title = Title, Findings = f, Body = Wrap(body, MaxLine) };
    }

    // ------------------------------------------------------------------ sub-sections

    private static void RemoteAddresses(StringBuilder b, List<ConnInfo> tcpRemote, NetSnapshot s, bool redact)
    {
        if (tcpRemote.Count == 0) return;
        var appOf = new Dictionary<ConnInfo, string>();   // ConnInfo has reference equality
        foreach (var a in s.Apps) foreach (var c in a.Conns) appOf[c] = a.Name;
        var groups = tcpRemote.GroupBy(c => c.Remote!.ToString())
            .Select(g => (addr: g.Key, where: g.First().Where, n: g.Count(), apps: g.Select(c => appOf.GetValueOrDefault(c, "?")).Distinct().ToList(),
                ip: g.First().Remote!))
            .OrderByDescending(x => x.n).ThenBy(x => x.addr, StringComparer.Ordinal).ToList();
        // When redacting, internet addresses would all read "[internet address]", so list only local/LAN/VPN ones and
        // say how many distinct internet addresses there were.
        var shown = redact ? groups.Where(x => !Redactor.IsPublic(x.ip)).ToList() : groups;
        int hiddenPublic = redact ? groups.Count(x => Redactor.IsPublic(x.ip)) : 0;
        b.Append(redact ? "\nBusiest non-internet addresses (by number of TCP connections):\n" : "\nBusiest remote addresses (by number of TCP connections):\n");
        if (shown.Count == 0) b.Append("- None.\n");
        foreach (var x in shown.Take(MaxRemotes))
        {
            string apps = string.Join(", ", x.apps.Take(3).Select(n => Clean(n, 30, redact))) + (x.apps.Count > 3 ? $" +{x.apps.Count - 3} more" : "");
            b.Append($"- {x.addr} ({AddressClass.Label(x.where)}): {x.n} connection{(x.n == 1 ? "" : "s")} – {apps}\n");
        }
        More(b, shown.Count - MaxRemotes, "address");
        if (hiddenPublic > 0) b.Append($"- Plus {hiddenPublic} distinct internet address{(hiddenPublic == 1 ? "" : "es")} (hidden for privacy).\n");
    }

    private static void ListeningPorts(StringBuilder b, NetSnapshot s, bool red)
    {
        var apps = s.Apps.Where(a => a.ListeningPorts > 0).OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
        b.Append("\nListening for incoming connections (TCP; \"any network\" = other devices can connect if the firewall allows):\n");
        if (apps.Count == 0) { b.Append("- Nothing is listening.\n"); return; }
        foreach (var a in apps.Take(MaxListenApps))
        {
            // One entry per port: IPv4 and IPv6 sockets on the same port are merged, keeping the widest reach.
            var ports = a.Conns.Where(c => c.IsListening).GroupBy(c => c.LocalPort)
                .Select(g => (port: g.Key, reach: g.Select(c => Reach(c.Where)).Max())).OrderBy(x => x.port).ToList();
            var byReach = ports.GroupBy(p => p.reach).OrderByDescending(g => g.Key)
                .Select(g =>
                {
                    var list = g.Select(p => p.port.ToString(C)).ToList();
                    string txt = string.Join(", ", list.Take(MaxPortsPerApp)) + (list.Count > MaxPortsPerApp ? $" +{list.Count - MaxPortsPerApp} more" : "");
                    return $"{txt} ({ReachText(g.Key)})";
                });
            b.Append($"- {Clean(a.Name, MaxNameChars, red)}: port{(ports.Count == 1 ? "" : "s")} {string.Join("; ", byReach)}\n");
        }
        More(b, apps.Count - MaxListenApps, "app");
    }

    /// <summary>Ordering for how widely a listening socket can be reached.</summary>
    private static int Reach(Where w) => w switch { Where.AnyNetwork => 4, Where.Internet => 3, Where.Lan => 2, Where.Shared => 2, _ => 1 };
    private static string ReachText(int r) => r switch { 4 => "any network", 3 => "public address", 2 => "LAN/VPN address only", _ => "this PC only" };

    // ------------------------------------------------------------------ helpers

    internal const int MaxLine = 108;

    /// <summary>
    /// Wrap lines longer than <paramref name="max"/> at the last "; ", ", " or space before it. Continuation lines are
    /// indented (two spaces, or to the text of a "- " bullet) so lists still read as lists.
    /// </summary>
    internal static string Wrap(string text, int max)
    {
        var sb = new StringBuilder(text.Length + 64);
        foreach (var raw in text.Split('\n'))
        {
            string line = raw;
            string indent = line.StartsWith("- ", StringComparison.Ordinal) ? "    " : line.StartsWith("  ", StringComparison.Ordinal) ? "    " : "  ";
            while (line.Length > max)
            {
                int cut = -1;
                foreach (var sep in new[] { "; ", ", ", " " })
                {
                    int i = line.LastIndexOf(sep, max - 1, StringComparison.Ordinal);
                    if (i > max / 3) { cut = i + sep.Length - 1; break; }   // keep the separator's punctuation on this line
                }
                if (cut < 0) cut = max;
                sb.Append(line[..cut].TrimEnd()).Append('\n');
                line = indent + line[cut..].TrimStart();
            }
            sb.Append(line).Append('\n');
        }
        return sb.ToString().TrimEnd('\n') + "\n";
    }

    /// <summary>"14:05" today, "Tue 14:05" within a week, else "3 Oct 14:05" (local time).</summary>
    internal static string When(DateTime utc, DateTime nowUtc)
    {
        var l = utc.ToLocalTime(); var n = nowUtc.ToLocalTime();
        if (l.Date == n.Date) return l.ToString("HH:mm", C);
        if ((n.Date - l.Date).TotalDays < 7) return l.ToString("ddd HH:mm", C);
        return l.ToString("d MMM HH:mm", C);
    }

    private static int Minutes(List<RateSample> s) => Math.Max(1, (int)Math.Round((s[^1].Utc - s[0].Utc).TotalMinutes));

    internal static string LinkText(AdapterInfo a)
    {
        if (a.RxLinkBps <= 0 && a.TxLinkBps <= 0) return "";
        if (a.RxLinkBps == a.TxLinkBps || a.TxLinkBps <= 0) return $", link {Bps(a.RxLinkBps)}";
        return $", link {Bps(a.RxLinkBps)} down / {Bps(a.TxLinkBps)} up";
    }

    /// <summary>Link speeds are in bits per second, decimal (as Windows shows them): "866 Mbps", "2.4 Gbps".</summary>
    internal static string Bps(long bps) =>
        bps >= 1_000_000_000 ? (bps / 1e9).ToString(bps % 1_000_000_000 == 0 ? "0" : "0.#", C) + " Gbps" :
        bps >= 1_000_000 ? (bps / 1e6).ToString("0", C) + " Mbps" :
        (bps / 1e3).ToString("0", C) + " kbps";

    private static string AppName(AppStat a, bool red) => Clean(a.Name, MaxNameChars, red) + (a.ProcCount > 1 ? $" ×{a.ProcCount}" : "");

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>
    /// Redact first, then shorten: cutting first could leave half an address ("81.2.69…") that the redactor no
    /// longer recognises. Every user-influenced value that gets trimmed goes through here.
    /// </summary>
    internal static string Clean(string raw, int max, bool redact) => Trim(redact ? Redactor.Apply(raw) : raw, max);

    // Windows' own adapter names (any language-neutral default), kept when redacting; anything else may be personal.
    private static readonly Regex[] DefaultAliases =
    {
        new(@"^Wi-?Fi( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Wireless Network Connection( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Ethernet( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Local Area Connection\*?( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Bluetooth Network Connection( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Cellular( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Mobile Broadband Connection( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^Loopback Pseudo-Interface( \d+)?$", RegexOptions.IgnoreCase),
        new(@"^(Tailscale|NordLynx|ProtonVPN|Mullvad|OpenVPN (TAP-Windows6|Wintun|Data Channel Offload)|Npcap Loopback Adapter)( \d+)?$", RegexOptions.IgnoreCase),
    };
    private static readonly Regex VEthernet = new(@"^vEthernet \(.*\)$", RegexOptions.IgnoreCase);

    /// <summary>
    /// The adapter's name for the report. Unredacted: as named. Redacted: Windows' default names as they are,
    /// "vEthernet (virtual switch)" for Hyper-V switches (the part in brackets is user-chosen), otherwise
    /// "Wi-Fi adapter (renamed)" etc.
    /// </summary>
    internal static string AdapterLabel(AdapterInfo a, bool redact)
    {
        string name = a.Name.Trim();
        if (!redact) return $"\"{Trim(name, 30)}\"";
        if (IsDefaultAlias(name)) return $"\"{name}\"";
        if (VEthernet.IsMatch(name)) return "\"vEthernet (virtual switch)\"";
        return $"{a.Kind} adapter (renamed)";
    }

    internal static bool IsDefaultAlias(string name) => name.Length <= 40 && DefaultAliases.Any(r => r.IsMatch(name.Trim()));

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    private static void More(StringBuilder b, int n, string what)
    {
        if (n > 0) b.Append($"- …and {n} more {what}{(n == 1 ? "" : what.EndsWith('s') ? "es" : "s")}\n");
    }
}
