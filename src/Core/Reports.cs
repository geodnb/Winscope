using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WinScope.Core;

/// <summary>How a report should be produced.</summary>
public sealed class ReportOptions
{
    /// <summary>Hide personal details: user/PC names, network names, hardware (MAC) addresses, internet IPs, profile paths.</summary>
    public bool Redact { get; init; } = true;
    /// <summary>When the report was requested (UTC).</summary>
    public DateTime NowUtc { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// One tool's part of a report. Plain text written for a person or an AI assistant to read: short, factual,
/// units on every number, local times. No ANSI, no tables wider than ~100 characters.
/// </summary>
public sealed class ReportSection
{
    /// <summary>e.g. "Performance".</summary>
    public string Title { get; init; } = "";
    /// <summary>
    /// The most important points, most serious first, one line each (e.g. "3 blue screens in the last 7 days
    /// (stop code 0x133 DPC_WATCHDOG_VIOLATION)"). Empty when nothing stands out. These are collected into the
    /// report's "Key findings" at the top. Don't pad with good news – but one reassuring line like
    /// "No problems found" is fine when it helps.
    /// </summary>
    public IReadOnlyList<string> Findings { get; init; } = Array.Empty<string>();
    /// <summary>The detailed section text (may contain blank lines and "- " bullet lists; no heading line).</summary>
    public string Body { get; init; } = "";
}

/// <summary>Where reports go. Clipboard is the default.</summary>
public enum ReportOutput { Clipboard = 0, File = 1, Both = 2 }

/// <summary>
/// Removes personal details from report text. Tools should call <see cref="Apply"/> on each raw value (before any
/// truncation) when <see cref="ReportOptions.Redact"/> is set, and use <see cref="NetworkName"/> for Wi-Fi names they
/// know about. The shell applies <see cref="Apply"/> to the whole report again as a safety net – it's idempotent:
/// text inside a placeholder this class produced is never rewritten.
/// Private (LAN) IPv4 addresses are kept – they're the same on millions of home networks and useful for diagnosis.
/// </summary>
public static class Redactor
{
    // Placeholders produced here. Every pass matches these first and leaves them untouched (idempotence).
    private const string KeepPattern =
        @"\[(?:user|PC name|domain|email|account ID|device serial|hardware address|internet address|Wi-Fi network \d+|address with hardware ID|private IPv6 address|" +
        @"(?:user|Documents|Desktop|Pictures|Videos|Music|Downloads) folder)\]|%(?:TEMP|LOCALAPPDATA|APPDATA|USERPROFILE|OneDrive|OneDriveConsumer|OneDriveCommercial)%";

    private const RegexOptions RO = RegexOptions.Compiled | RegexOptions.CultureInvariant;
    // IPv6 runs before the MAC patterns so "2001:db8:aa:bb:cc:dd:ee:ff" isn't mistaken for a MAC.
    private static readonly Regex V6 = new(@"(?<![0-9A-Fa-f:])(?:[0-9A-Fa-f]{0,4}:){2,8}[0-9A-Fa-f]{0,4}(?:%\w+)?(?![0-9A-Fa-f:]|\.\d)", RO);
    private static readonly Regex[] Macs =
    {
        new(@"(?<![0-9A-Fa-f:])[0-9A-Fa-f]{2}(?::[0-9A-Fa-f]{2}){5}(?![0-9A-Fa-f:])", RO),               // 00:1a:2b:3c:4d:5e
        new(@"(?<![0-9A-Fa-f-])[0-9A-Fa-f]{2}(?:-[0-9A-Fa-f]{2}){5}(?![0-9A-Fa-f]|-[0-9A-Fa-f])", RO),   // 00-1A-2B-3C-4D-5E
        new(@"(?<![0-9A-Fa-f.])[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}\.[0-9A-Fa-f]{4}(?![0-9A-Fa-f]|\.[0-9A-Fa-f])", RO), // 001a.2b3c.4d5e
        // 001A2B3C4D5E, 0x001A2B3C4D5E, Dev_A4C3F0123456 – 12 hex digits containing both a letter and a digit.
        // Not after "-": the last group of a GUID ({…-50F6B8EA6B54}) is 12 hex digits too.
        new(@"(?<![0-9A-Za-z-])(?:0x)?(?=[0-9A-Fa-f]{0,11}[A-Fa-f])(?=[0-9A-Fa-f]{0,11}[0-9])[0-9A-Fa-f]{12}(?![0-9A-Za-z])", RO),
    };
    private static readonly Regex TrailingGap = new(@"[\s.]*$", RO);
    private static readonly Regex RetinaName = new(@"@\d+(?:\.\d+)?x\.(?:png|jpe?g|gif|webp|svg|bmp|ico)$", RO | RegexOptions.IgnoreCase);
    private static readonly Regex V4 = new(@"(?<![\d.])(?:\d{1,3}\.){3}\d{1,3}(?!\.?\d)", RO);
    private static readonly Regex VersionBefore = new(@"\b(?:version|ver|v|build|release|rev)\s*:?\s*$", RO | RegexOptions.IgnoreCase);

    private static readonly object Lock = new();
    private static readonly Dictionary<string, string> Networks = new(StringComparer.Ordinal);
    private static Regex? _main;
    private static int _mainNetworks = -1;
    private static Dictionary<string, string> _litMap = new(), _wordMap = new();

    /// <summary>The stand-in for a network mentioned in different letter case ("taylor home 5g"). Caller holds Lock.</summary>
    private static string NetworkIgnoringCase(string s) =>
        Networks.FirstOrDefault(kv => kv.Key.Equals(s, StringComparison.OrdinalIgnoreCase)).Value ?? "[Wi-Fi network]";

    /// <summary>A stable stand-in for a Wi-Fi network name ("[Wi-Fi network 1]"), consistent within the session.</summary>
    public static string NetworkName(string ssid)
    {
        if (string.IsNullOrEmpty(ssid)) return "(hidden network)";
        lock (Lock)
        {
            if (!Networks.TryGetValue(ssid, out var a)) Networks[ssid] = a = $"[Wi-Fi network {Networks.Count + 1}]";
            return a;
        }
    }

    public static string Apply(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        var (main, lits, words) = MainPattern();
        // One pass for everything name-like, so a replacement can never be matched again by a later rule.
        text = main.Replace(text, m =>
        {
            if (m.Groups["keep"].Success) return m.Value;
            if (m.Groups["email"].Success) return RetinaName.IsMatch(m.Value) ? m.Value : "[email]";   // icon@2x.png is a file
            if (m.Groups["sid"].Success) return "[account ID]";
            if (m.Groups["devhead"].Success) return m.Groups["devhead"].Value + "[device serial]";
            if (m.Groups["lit"].Success) return lits.TryGetValue(NormPath(m.Value), out var l) ? l : "[user folder]";
            if (m.Groups["gp"].Success) return m.Groups["gp"].Value + "[user]" + TrailingGap.Match(m.Value).Value;   // keep a sentence's "." / spacing
            if (m.Groups["word"].Success) return words.TryGetValue(m.Value.ToLowerInvariant(), out var w) ? w : "[user]";
            if (m.Groups["part"].Success) return "[user]";
            if (m.Groups["net"].Success) { lock (Lock) return Networks.TryGetValue(m.Value, out var n) ? n : NetworkIgnoringCase(m.Value); }
            if (m.Groups["vol"].Success) return "Volume{…}";
            return m.Value;
        });
        text = V6.Replace(text, m =>
        {
            // "address:2a00:1450::200e" – the match starts at the label's colon; check the address without it.
            string v = m.Value, lead = "";
            if (v.StartsWith(':') && !v.StartsWith("::")) { lead = ":"; v = v[1..]; }
            if (v.Count(c => c == ':') < 2 || !IPAddress.TryParse(v, out var ip) || ip.AddressFamily != AddressFamily.InterNetworkV6) return m.Value;
            if (ip.IsIPv4MappedToIPv6) return IsPublic(ip) ? lead + "[internet address]" : m.Value;
            if (IsPublic(ip)) return lead + "[internet address]";
            var b = ip.GetAddressBytes();
            if (b[11] == 0xFF && b[12] == 0xFE) return lead + "[address with hardware ID]";   // EUI-64: interface ID is the MAC
            if ((b[0] & 0xFE) == 0xFC) return lead + "[private IPv6 address]";                 // ULA: a stable per-network ID
            return m.Value;
        });
        foreach (var mac in Macs) text = mac.Replace(text, "[hardware address]");
        text = V4.Replace(text, m => ParseV4(m.Value) is IPAddress ip && IsPublic(ip) && !LooksLikeVersion(text, m.Index) ? "[internet address]" : m.Value);
        return text;
    }

    /// <summary>Dotted IPv4, allowing leading zeros ("081.002.069.160", which IPAddress.TryParse rejects).</summary>
    private static IPAddress? ParseV4(string s)
    {
        var parts = s.Split('.');
        if (parts.Length != 4) return null;
        var b = new byte[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i], NumberStyles.None, CultureInfo.InvariantCulture, out int v) || v > 255) return null;
            else b[i] = (byte)v;
        return new IPAddress(b);
    }

    /// <summary>"version 1.0.0.0" / "v2.3.4.5" / "build 10.0.1.2" are version numbers, not addresses ("server 1.2.3.4" is an address).</summary>
    private static bool LooksLikeVersion(string text, int at)
    {
        int from = Math.Max(0, at - 16);
        return VersionBefore.IsMatch(text[from..at]);
    }

    private static string NormPath(string p) => p.Replace('/', '\\').TrimEnd('\\').ToLowerInvariant();

    /// <summary>
    /// Names too generic (or too much part of the report's own wording) to replace as words – replacing them would
    /// mangle ordinary text. Profile paths containing them are still replaced.
    /// </summary>
    private static readonly HashSet<string> Generic = new(StringComparer.OrdinalIgnoreCase)
    {
        "admin", "administrator", "user", "users", "owner", "pc", "home", "guest", "default", "public", "desktop", "laptop",
        "windows", "system", "computer", "workstation", "server", "local", "localhost", "network", "internet", "wifi", "wi-fi",
        "wlan", "ethernet", "lan", "wan", "router", "modem", "storage", "performance", "events", "event", "report", "microsoft",
        "intel", "amd", "nvidia", "ryzen", "geforce", "radeon", "realtek", "qualcomm", "broadcom", "mediatek", "killer", "dell",
        "hp", "lenovo", "asus", "acer", "msi", "gigabyte", "samsung", "apple", "google", "android", "iphone", "gaming", "office",
        "work", "family", "test", "dev", "azuread", "microsoftaccount", "workgroup", "builtin", "file", "files", "disk", "drive",
        "cpu", "gpu", "ram", "memory", "app", "apps", "error", "warning", "update", "driver", "service", "time", "last", "free",
        "used", "total", "size", "peak", "average", "current", "download", "upload", "connection", "adapter", "cleanup", "temp",
        "cache", "hidden", "address", "name", "folder", "the", "and", "for", "are", "but", "not", "you", "all", "any", "can",
        "had", "her", "was", "one", "our", "out", "day", "get", "has", "him", "his", "how", "man", "new", "now", "old", "see",
        "two", "way", "who", "did", "its", "let", "put", "say", "she", "too", "use", "to", "of", "in", "it", "is", "on", "at",
        "be", "by", "or", "as", "an", "if", "no", "so", "up", "we", "me", "my", "do", "go", "he", "us", "ok",
        // Dates in the report ("Fri 30 Jan") and the report's own wording.
        "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec", "january", "february",
        "march", "april", "june", "july", "august", "september", "october", "november", "december", "mon", "tue", "wed",
        "thu", "fri", "sat", "sun", "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday", "today",
        "page", "power", "read", "long", "will", "short", "high", "low", "fast", "slow", "start", "stop", "crash", "blue",
        "green", "red", "white", "black", "small", "large", "big", "top", "best", "good", "fair", "poor", "more", "less",
        "about", "after", "before", "since", "until", "never", "always", "here", "there", "this", "that", "with", "from",
        // Router and vendor defaults used as network names.
        "netgear", "tp-link", "tplink", "linksys", "d-link", "dlink", "huawei", "zte", "xfinity", "virgin", "sky", "bt",
        "eduroam", "starlink", "guest", "5g", "2.4g", "ax", "ac",
    };

    /// <summary>True when every word of a name is generic or a number ("Wi-Fi 6", "NETGEAR 5G") – nothing to hide.</summary>
    private static bool IsGenericPhrase(string s)
    {
        var parts = Regex.Split(s.Trim(), @"[\s_()\[\]]+").Where(p => p.Length > 0).ToArray();
        return parts.Length == 0 || parts.All(p => Generic.Contains(p) || p.All(c => char.IsDigit(c) || c == '.') || Regex.IsMatch(p, @"^\d+(\.\d+)?(g|ghz|e)?$", RegexOptions.IgnoreCase));
    }

    /// <summary>Paths and whole names (matched ignoring case), and single name parts (matched case-sensitively, so
    /// a surname "Page" doesn't swallow "page file").</summary>
    private sealed record Identity(List<(string path, string label)> Paths, List<(string word, string label)> Words, List<string> Parts);

    /// <summary>Gather the identity in the background at startup: some lookups (domain display name, short names of a
    /// redirected folder on an offline share) can wait on the network, and reports are made on the UI thread.</summary>
    public static void Warm() => Task.Run(() => { try { _ = Ident.Value; } catch { } });
    private static readonly Lazy<Identity> Ident = new(BuildIdentity, LazyThreadSafetyMode.ExecutionAndPublication);

    private static (Regex, Dictionary<string, string>, Dictionary<string, string>) MainPattern()
    {
        lock (Lock)
        {
            if (_main != null && _mainNetworks == Networks.Count) return (_main, _litMap, _wordMap);
            var id = Ident.Value;
            var lits = new Dictionary<string, string>();
            foreach (var (p, l) in id.Paths) lits.TryAdd(NormPath(p), l);
            var words = new Dictionary<string, string>();
            foreach (var (w, l) in id.Words) words.TryAdd(w.ToLowerInvariant(), l);

            static string PathRx(string p) => Regex.Escape(p.TrimEnd('\\')).Replace(@"\\", @"[\\/]");
            var sb = new StringBuilder();
            sb.Append("(?<keep>").Append(KeepPattern).Append(')');
            // Bounded and anchored at the start of a run, so long runs of letters/digits can't make this quadratic.
            sb.Append(@"|(?<email>(?<![\w.+-])[\w.+-]{1,64}(?:@|%40)[\w-]{1,63}(?:\.[\w-]{1,63}){0,8}\.[A-Za-z]{2,24}(?![\w-]))");
            if (lits.Count > 0)
                sb.Append("|(?<lit>").Append(string.Join("|", id.Paths.Select(x => x.path).Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderByDescending(p => p.Length).Select(PathRx))).Append(@")(?=[\\/]|$|[\s""',;:)\]]|\.(?:\s|$))");
            // Anyone's profile folder: C:\Users\<name> (also \\?\C:\…, forward slashes, 8.3 short names like SAMTAY~1).
            sb.Append(@"|(?<gp>(?:\\\\\?\\)?[A-Za-z]:[\\/]Users[\\/])(?!(?:Public|Default|Default User|All Users)(?:[\\/]|\s|$)|\[user\])")
              // With a separator after it, the name runs to the separator. Without one ("C:\Users\Sarah Smith"), take the
              // rest of the phrase up to punctuation – over-redacting a few words beats leaking a surname.
              .Append(@"(?:[^\\/:*?""<>|\r\n]{1,64}(?=[\\/])|[^\\/:*?""<>|\r\n,;)\]'(]{1,64})");
            // Wi-Fi names before personal names: "Taylor Home 5G" should become one network placeholder, not "[user] Home 5G".
            var nets = Networks.Keys.Where(k => k.Length >= 2 && !IsGenericPhrase(k)).OrderByDescending(k => k.Length).ToList();
            if (nets.Count > 0)
                sb.Append(@"|(?<net>(?<!\w)(?:").Append(string.Join("|", nets.Select(Regex.Escape))).Append(@")(?!\w))");
            if (words.Count > 0)
                // Bounded by letters only, so "sam2", "sam_pc" and "%20Taylor%20" are caught too.
                sb.Append(@"|(?<word>(?<!\p{L})(?:").Append(string.Join("|", words.Keys.OrderByDescending(w => w.Length).Select(Regex.Escape))).Append(@")(?!\p{L}))");
            // Name parts as written and in capitals ("Taylor", "TAYLOR") – never lower-case, which is where ordinary words live.
            var parts = id.Parts.Where(x => !words.ContainsKey(x.ToLowerInvariant())).SelectMany(x => new[] { x, x.ToUpperInvariant() })
                .Distinct(StringComparer.Ordinal).OrderByDescending(x => x.Length).ToList();
            if (parts.Count > 0)
                sb.Append(@"|(?<part>(?<!\p{L})(?-i:").Append(string.Join("|", parts.Select(Regex.Escape))).Append(@")(?!\p{L}))");
            // Windows account IDs (local/domain S-1-5-21-…, Entra S-1-12-1-…) are unique to the person.
            sb.Append(@"|(?<sid>(?<![\w-])S-1-(?:5-21|12-1)(?:-\d{1,10}){3,6}(?![\w-]))");
            // Device paths carry serial numbers: \\?\USB#VID_0781&PID_5581#<serial>#{…} and USB\VID_0781&PID_5581\<serial>.
            sb.Append(@"|(?<devhead>(?<![\w&])(?:\\\\\?\\|\\\?\?\\)?(?-i:USB|USBSTOR|HID|BTHENUM|BTHLE\w*|BTH\w*|SWD|SCSI|STORAGE|NVME|USBPRINT|WPDBUSENUM)[#\\][^#\\\s]{1,200}[#\\])(?![#\\\s{]|\[device serial\])[^#\\\s]{4,200}");
            sb.Append(@"|(?<vol>Volume\{[0-9A-Fa-f-]{36}\})");
            _main = new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            _mainNetworks = Networks.Count;
            _litMap = lits; _wordMap = words;
            return (_main, lits, words);
        }
    }

    /// <summary>Names and folders that identify this person and PC. Gathered once; every lookup fails safe.</summary>
    private static Identity BuildIdentity()
    {
        var paths = new List<(string, string)>();
        var words = new List<(string, string)>();
        var parts = new List<string>();
        void P(string? p, string label)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(p)) return;
                p = Environment.ExpandEnvironmentVariables(p.Trim()).TrimEnd('\\');
                if (p.Length < 4) return;
                paths.Add((p, label));
                // Never ask for short names on a network share: an offline server makes the call wait.
                if (!p.StartsWith(@"\\") && ShortPath(p) is string s && !s.Equals(p, StringComparison.OrdinalIgnoreCase)) paths.Add((s.TrimEnd('\\'), label));
            }
            catch { }
        }
        void W(string? w, string label)
        {
            if (string.IsNullOrWhiteSpace(w)) return;
            w = w.Trim();
            if (w.Length >= 2 && !Generic.Contains(w)) words.Add((w, label));
        }
        void Person(string? fullName)
        {
            if (string.IsNullOrWhiteSpace(fullName)) return;
            W(fullName, "[user]");
            // "Taylor, Sam" and "Sam (IT)" too. Parts match case-sensitively and skip ordinary words.
            foreach (var part in Regex.Split(fullName, @"[\s,()]+"))
                if (part.Length >= 3 && !Generic.Contains(part) && !part.Equals(fullName.Trim(), StringComparison.Ordinal)) parts.Add(part);
        }

        string? profile = null;
        Try(() => profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Try(() => P(Path.GetTempPath(), "%TEMP%"));
        Try(() => P(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "%LOCALAPPDATA%"));
        Try(() => P(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "%APPDATA%"));
        Try(() => P(profile, "%USERPROFILE%"));
        Try(() => P(Environment.GetEnvironmentVariable("OneDrive"), "%OneDrive%"));
        Try(() => P(Environment.GetEnvironmentVariable("OneDriveConsumer"), "%OneDriveConsumer%"));
        Try(() => P(Environment.GetEnvironmentVariable("OneDriveCommercial"), "%OneDriveCommercial%"));
        // Known folders can be redirected outside the profile (another drive, OneDrive): give them their own label.
        void Known(string? p, string label)
        {
            if (string.IsNullOrWhiteSpace(p)) return;
            if (profile != null && p.StartsWith(profile.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)) return;
            P(p, label);
        }
        Try(() => Known(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "[Documents folder]"));
        Try(() => Known(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "[Desktop folder]"));
        Try(() => Known(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "[Pictures folder]"));
        Try(() => Known(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "[Videos folder]"));
        Try(() => Known(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "[Music folder]"));
        Try(() => Known(KnownFolder(new Guid("374DE290-123F-4565-9164-39C4925E467B")), "[Downloads folder]"));

        Try(() => Person(Environment.UserName));
        Try(() => Person(DisplayName()));
        Try(() => Person(FullName(Environment.UserName)));
        // Elevated as a different admin account: the person at the desktop is someone else – hide them too.
        var (deskUser, deskDomain) = Elevation.DesktopUser;
        if (!string.IsNullOrWhiteSpace(deskUser) && !deskUser.Equals(Environment.UserName, StringComparison.OrdinalIgnoreCase))
        {
            Person(deskUser);
            Try(() => Person(FullName(deskUser)));
            Try(() => P(ProfileOf(deskUser, deskDomain), "[user folder]"));
        }

        Try(() => W(Environment.MachineName, "[PC name]"));
        Try(() => W(Dns.GetHostName(), "[PC name]"));
        Try(() =>
        {
            var g = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties();
            W(g.HostName, "[PC name]");
            if (!string.IsNullOrWhiteSpace(g.DomainName)) { W(g.HostName + "." + g.DomainName, "[PC name]"); W(g.DomainName, "[domain]"); }
        });
        foreach (var d in new[] { Try(() => Environment.UserDomainName), deskDomain })
            if (!string.IsNullOrWhiteSpace(d) && !d.Equals(Try(() => Environment.MachineName), StringComparison.OrdinalIgnoreCase)
                && !d.Equals("WORKGROUP", StringComparison.OrdinalIgnoreCase) && !d.Contains(' '))
                W(d, "[domain]");

        return new Identity(paths, words, parts);
    }

    private static void Try(Action a) { try { a(); } catch { } }
    private static string? Try(Func<string?> f) { try { return f(); } catch { return null; } }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetShortPathNameW(string longPath, StringBuilder shortPath, uint size);

    private static string? ShortPath(string p)
    {
        var sb = new StringBuilder(520);
        uint n = GetShortPathNameW(p, sb, (uint)sb.Capacity);
        return n > 0 && n < sb.Capacity ? sb.ToString() : null;
    }

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid id, uint flags, IntPtr token, out IntPtr path);

    private static string? KnownFolder(Guid id)
    {
        if (SHGetKnownFolderPath(id, 0, IntPtr.Zero, out var p) != 0) { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); return null; }
        try { return Marshal.PtrToStringUni(p); } finally { Marshal.FreeCoTaskMem(p); }
    }

    [DllImport("secur32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static extern bool GetUserNameExW(int format, StringBuilder name, ref uint size);

    /// <summary>The account's display name ("Sam Taylor") where Windows knows one (domain / Entra accounts).</summary>
    private static string? DisplayName()
    {
        const int NameDisplay = 3;
        uint size = 256;
        var sb = new StringBuilder((int)size);
        return GetUserNameExW(NameDisplay, sb, ref size) ? sb.ToString() : null;
    }

    [DllImport("netapi32.dll", CharSet = CharSet.Unicode)]
    private static extern int NetUserGetInfo(string? server, string user, int level, out IntPtr buffer);

    [DllImport("netapi32.dll")]
    private static extern int NetApiBufferFree(IntPtr buffer);

    /// <summary>A local account's "full name" (USER_INFO_10.usri10_full_name). Local accounts only; never contacts a server.</summary>
    private static string? FullName(string user)
    {
        if (NetUserGetInfo(null, user, 10, out var buf) != 0 || buf == IntPtr.Zero) return null;
        try { return Marshal.PtrToStringUni(Marshal.ReadIntPtr(buf, 3 * IntPtr.Size)); }
        finally { NetApiBufferFree(buf); }
    }

    private static string? ProfileOf(string user, string? domain)
    {
        var acct = string.IsNullOrEmpty(domain) ? new System.Security.Principal.NTAccount(user) : new System.Security.Principal.NTAccount(domain, user);
        string sid = acct.Translate(typeof(System.Security.Principal.SecurityIdentifier)).Value;
        using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + sid);
        return k?.GetValue("ProfileImagePath") as string;
    }

    /// <summary>True for addresses on the public internet (not private, loopback, link-local, CGNAT, ULA, multicast).</summary>
    public static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            if (b[0] is 0 or 10 or 127 || b[0] >= 224) return false;
            if (b[0] == 169 && b[1] == 254) return false;
            if (b[0] == 172 && b[1] is >= 16 and <= 31) return false;
            if (b[0] == 192 && b[1] == 168) return false;
            if (b[0] == 100 && b[1] is >= 64 and <= 127) return false;
            return true;
        }
        if (ip.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (IPAddress.IsLoopback(ip) || ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6Multicast || ip.Equals(IPAddress.IPv6Any)) return false;
            var b = ip.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC) return false;   // fc00::/7 unique local
            return true;
        }
        return false;
    }
}

/// <summary>The "About this PC" block at the top of every report.</summary>
public static class SystemSummary
{
    public static string Build(ReportOptions o)
    {
        var sb = new StringBuilder();
        void Line(string k, string? v) { if (!string.IsNullOrWhiteSpace(v)) sb.Append("- ").Append(k).Append(": ").Append(v).Append('\n'); }
        Line("Windows", WindowsVersion());
        Line("CPU", CpuName() is string cpu ? $"{cpu} ({Environment.ProcessorCount} logical processors)" : $"{Environment.ProcessorCount} logical processors");
        Line("Memory", TotalRam() is ulong ram ? UI.Format.Bytes(ram) : null);
        Line("Graphics", string.Join("; ", DisplayAdapters()));
        Line("Time since Windows started", UI.Format.Span(TimeSpan.FromMilliseconds(Environment.TickCount64)) + " (includes sleep; with Fast Startup, shutting down doesn't reset this)");
        Line("WinScope", $"{typeof(SystemSummary).Assembly.GetName().Version?.ToString(3)}, {(Elevation.IsAdmin ? "running as administrator" : "not elevated")}");
        if (!o.Redact) Line("PC name", Environment.MachineName);
        Line("Units", "sizes and speeds in KB/MB/GB use 1 KB = 1024 bytes; network link speeds in Mbps/Gbps are decimal bits per second");
        return sb.ToString();
    }

    private static string? WindowsVersion()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            string? product = k?.GetValue("ProductName") as string, display = k?.GetValue("DisplayVersion") as string, build = k?.GetValue("CurrentBuild") as string;
            object? ubr = k?.GetValue("UBR");
            // ProductName still says "Windows 10" on Windows 11; the build number tells them apart.
            if (product != null && int.TryParse(build, out int b) && b >= 22000) product = product.Replace("Windows 10", "Windows 11");
            return $"{product} {display} (build {build}{(ubr is int u ? "." + u.ToString(CultureInfo.InvariantCulture) : "")})".Trim();
        }
        catch { return RuntimeInformation.OSDescription; }
    }

    private static string? CpuName()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (k?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        catch { return null; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    private static ulong? TotalRam()
    {
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref m) ? m.ullTotalPhys : null;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DISPLAY_DEVICEW
    {
        public int cb;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string DeviceName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceString;
        public int StateFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceID;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string DeviceKey;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevicesW(string? device, uint devNum, ref DISPLAY_DEVICEW dd, uint flags);

    /// <summary>Distinct graphics adapter names (e.g. "NVIDIA GeForce RTX 4080").</summary>
    private static IEnumerable<string> DisplayAdapters()
    {
        var names = new List<string>();
        try
        {
            for (uint i = 0; i < 16; i++)
            {
                var dd = new DISPLAY_DEVICEW { cb = Marshal.SizeOf<DISPLAY_DEVICEW>() };
                if (!EnumDisplayDevicesW(null, i, ref dd, 0)) break;
                const int MIRRORING = 0x8;
                if ((dd.StateFlags & MIRRORING) != 0 || string.IsNullOrWhiteSpace(dd.DeviceString)) continue;
                if (dd.DeviceString.Contains("Basic Render", StringComparison.OrdinalIgnoreCase)) continue;
                if (!names.Contains(dd.DeviceString)) names.Add(dd.DeviceString.Trim());
            }
        }
        catch { }
        return names;
    }
}

/// <summary>Assembles reports and sends them to the clipboard and/or a file.</summary>
public static class ReportService
{
    public static string ReportsFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "WinScope Reports");

    /// <summary>Full report (all sections) or a single tool's report.</summary>
    public static string Compose(IEnumerable<(string title, Func<ReportOptions, ReportSection?> build)> tools, ReportOptions o, string? onlyTitle)
    {
        var sections = new List<ReportSection>();
        foreach (var (title, build) in tools)
        {
            ReportSection? s;
            try { s = build(o); }
            catch (Exception ex) { App.LogError(ex); s = new ReportSection { Title = title, Body = "(This part of the report couldn't be produced: " + ex.Message + ")" }; }
            if (s != null) sections.Add(s);
        }

        var sb = new StringBuilder();
        string when = o.NowUtc.ToLocalTime().ToString("ddd d MMM yyyy, HH:mm", CultureInfo.InvariantCulture);
        string tz = TimeZoneInfo.Local.IsDaylightSavingTime(o.NowUtc.ToLocalTime()) ? TimeZoneInfo.Local.DaylightName : TimeZoneInfo.Local.StandardName;
        sb.Append("# WinScope ").Append(onlyTitle != null ? onlyTitle + " report" : "system report").Append('\n');
        sb.Append($"Created {when} ({tz}). Times below are local.\n");
        sb.Append("This is a plain-text snapshot of what this Windows PC has been doing, made to be pasted into an AI assistant or sent to someone helping with a problem.\n");
        if (o.Redact)
            sb.Append("Personal details were removed automatically: user, account and PC names, email addresses, Wi-Fi network names, ")
              .Append("hardware (MAC) addresses and device serial numbers, internet IP addresses, and the names of personal files and folders. ")
              .Append("Automatic removal can miss things – read it through before posting it publicly.\n");
        else
            sb.Append("Personal details are INCLUDED (user and PC names, network names, file names, addresses) – don't post this publicly.\n");
        sb.Append("\n## About this PC\n").Append(SystemSummary.Build(o));

        var findings = sections.SelectMany(s => s.Findings.Select(f => (s.Title, f))).ToList();
        sb.Append("\n## Key findings\n");
        if (findings.Count == 0) sb.Append("- Nothing stands out.\n");
        foreach (var (t, f) in findings) sb.Append("- ").Append(onlyTitle == null ? $"[{t}] " : "").Append(f).Append('\n');

        foreach (var s in sections)
        {
            sb.Append("\n## ").Append(s.Title).Append('\n');
            sb.Append(s.Body.TrimEnd()).Append('\n');
        }
        sb.Append("\n(End of report.)\n");
        string text = sb.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
        return o.Redact ? Redactor.Apply(text) : text;
    }

    /// <summary>
    /// Save the report file (if wanted) off the UI thread – Documents may be redirected or offline – then copy to the
    /// clipboard on the UI thread. Returns a one-line status for the UI, and the saved file path (if any).
    /// While elevated, files aren't written (the user's Documents folder could be redirected), so it falls back to
    /// the clipboard.
    /// </summary>
    public static async Task<(string message, string? path)> DeliverAsync(string text, ReportOutput output, string fileTag)
    {
        bool wantClip = output != ReportOutput.File;
        bool wantFile = output != ReportOutput.Clipboard;
        bool elevatedNoFile = wantFile && !UserFiles.WriteAllowed;
        if (elevatedNoFile) { wantFile = false; wantClip = true; }

        string? path = null, fileErr = null;
        if (wantFile)
        {
            var stamp = DateTime.Now;
            (path, fileErr) = await Task.Run(() => SaveFile(text, fileTag, stamp));
            if (path == null) wantClip = true;   // the file failed: at least put it on the clipboard
        }
        bool clipped = wantClip && TrySetClipboard(text);

        string size = UI.Format.Bytes(Encoding.UTF8.GetByteCount(text));
        var parts = new List<string>();
        if (clipped) parts.Add($"Report copied to the clipboard ({size}) – paste it into an AI assistant, an email or a forum post");
        if (path != null) parts.Add("saved to Documents\\WinScope Reports");
        if (fileErr != null) parts.Add("couldn't save the file: " + fileErr);
        if (elevatedNoFile) parts.Add("files aren't saved while running as administrator");
        if (wantClip && !clipped) parts.Add(path != null
            ? "couldn't also copy it to the clipboard (another app may be using it)"
            : "couldn't copy the report – another app may be using the clipboard; try again");
        string msg = string.Join(" · ", parts);
        return (char.ToUpperInvariant(msg[0]) + msg[1..] + ".", path);
    }

    private static (string? path, string? error) SaveFile(string text, string fileTag, DateTime stamp)
    {
        try
        {
            Directory.CreateDirectory(ReportsFolder);
            string baseName = $"WinScope {fileTag} report {stamp:yyyy-MM-dd HHmmss}";
            for (int i = 0; i < 100; i++)
            {
                string path = Path.Combine(ReportsFolder, baseName + (i == 0 ? "" : $" ({i + 1})") + ".txt");
                FileStream fs;
                // Only "that name is taken" moves on to the next name; any other error (disk full, share gone) is reported.
                try { fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None); }
                catch (IOException ex) when (ex.HResult is unchecked((int)0x80070050) or unchecked((int)0x800700B7)) { continue; }
                try
                {
                    using (fs)
                    using (var w = new StreamWriter(fs, new UTF8Encoding(true)))
                        w.Write(text);
                    return (path, null);
                }
                catch
                {
                    try { File.Delete(path); } catch { }   // don't leave an empty or half-written report behind
                    throw;
                }
            }
            return (null, "too many reports with the same name");
        }
        catch (Exception ex) { return (null, ex.Message); }
    }

    /// <summary>
    /// WPF already retries opening and flushing the clipboard internally, so call it once (looping here would freeze the
    /// UI for seconds). If it throws, check whether the text actually landed – a failed flush after a successful set is fine.
    /// </summary>
    private static bool TrySetClipboard(string text)
    {
        try { System.Windows.Clipboard.SetDataObject(text, true); return true; }
        catch (ExternalException)
        {
            try { return System.Windows.Clipboard.ContainsText() && System.Windows.Clipboard.GetText() == text; }
            catch { return false; }
        }
    }
}
