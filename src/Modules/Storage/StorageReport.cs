using System.Globalization;
using System.Text;
using WinScope.UI;

namespace WinScope.Modules.Storage;

// Pure report builder (no WPF, no I/O): snapshot records in, text out – unit-tested in the scratch project.

public sealed record ReportDrive(string Letter, string Label, string FileSystem, long Total, long Free, long Used, bool Removable);

public sealed record ReportCategory(
    string Name, bool ReportOnly, bool IsRecycleBin, bool Analysed, long Files, long Bytes, bool Cleanable, bool DefaultOn,
    bool AdminOnly, bool PartAdmin, bool Present, IReadOnlyList<string> Notes);

public sealed record ReportFolder(string Path, long Bytes, long Files, bool IsLink);
public sealed record ReportFile(string Path, long Bytes, DateTime WriteUtc);

public sealed record ReportScan(
    string RootPath, DateTime FinishedUtc, TimeSpan Took, long Bytes, long Files, long Dirs, long Links, long Unreadable,
    long CloudFiles, long CloudBytes, bool Cancelled, long? DriveUsed, IReadOnlyList<ReportFolder> TopFolders,
    IReadOnlyList<ReportFile> TopFiles, int TotalFolders);

public sealed record ReportCleanItem(string Category, long Files, long Bytes, long PendingFiles, long PendingBytes,
    long Skipped, string SkipWhy, bool Cancelled, bool StoppedForGrowth);

public sealed record ReportCleanRun(DateTime WhenUtc, IReadOnlyList<ReportCleanItem> Items);

public sealed class StorageReportData
{
    public IReadOnlyList<ReportDrive> Drives { get; init; } = Array.Empty<ReportDrive>();
    public bool DrivesLoaded { get; init; }
    public IReadOnlyList<ReportCategory> Categories { get; init; } = Array.Empty<ReportCategory>();
    public DateTime? AnalysedUtc { get; init; }
    public bool Analysing { get; init; }
    public long? SafeBytes { get; init; }
    public bool IsAdmin { get; init; }
    public bool IsDesktopUser { get; init; } = true;
    public ReportScan? Scan { get; init; }
    public bool ScanRunning { get; init; }
    public IReadOnlyList<ReportCleanRun> CleanRuns { get; init; } = Array.Empty<ReportCleanRun>();
}

public static class StorageReport
{
    public const int MaxLine = 108;
    public const int MaxDrives = 12, MaxFolders = 10, MaxFiles = 10, MaxRuns = 5, MaxNotes = 3;
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;
    private const long GB = 1L << 30;

    /// <summary>
    /// Findings (most serious first, at most 5) and the body text. <paramref name="redact"/> is applied to every
    /// path and note (production: Redactor.Apply) when <paramref name="redacting"/> is set; drive labels are dropped.
    /// </summary>
    public static (List<string> findings, string body) Build(StorageReportData d, DateTime nowUtc, bool redacting, Func<string, string> redact)
    {
        string R(string s) => redacting ? redact(s) : s;
        // Paths: redact first, then hide personal names (P8), then shorten (P11) – never the other way round.
        string PF(string p) => ShortPath(redacting ? ReportPaths.File(redact(p)) : p);
        string PD(string p) => ShortPath(redacting ? ReportPaths.Folder(redact(p)) : p);
        var findings = Findings(d);
        var sb = new StringBuilder();

        // ---- drives
        sb.Append("Drives (sizes in binary units: 1 GB = 1024 MB):\n");
        if (d.Drives.Count == 0) sb.Append(d.DrivesLoaded ? "- No fixed or removable drives were found.\n" : "- Not read yet.\n");
        foreach (var dr in d.Drives.Take(MaxDrives))
        {
            string label = !redacting && !string.IsNullOrWhiteSpace(dr.Label) ? $" \"{Clip(dr.Label, 32)}\"" : "";
            string kind = dr.Removable ? "removable" : "fixed";
            sb.Append($"- {dr.Letter}{label} ({dr.FileSystem}, {kind}): {B(dr.Total)} total, {B(dr.Free)} free, {Pct(dr.Used, dr.Total)} used\n");
        }
        if (d.Drives.Count > MaxDrives) sb.Append($"- and {d.Drives.Count - MaxDrives} more drives\n");

        // ---- cleanup estimate
        sb.Append('\n');
        if (d.AnalysedUtc is DateTime at)
            sb.Append($"Cleanup estimate (checked {Time(at, nowUtc)}{(d.Analysing ? ", re-checking now" : "")}). " +
                      "Nothing is deleted unless it's listed under \"Cleanups this session\":\n");
        else
            sb.Append("Cleanup estimate: still being checked" + (d.Categories.Any(c => c.Analysed) ? " (partial figures below):\n" : ".\n"));
        if (d.SafeBytes is long safe)
            AppendWrapped(sb, $"- About {B(safe)} can be cleaned safely (categories that are on by default and allowed " +
                              $"{(d.IsAdmin ? "as administrator" : "without administrator rights")})");
        foreach (var c in d.Categories.Where(c => !c.ReportOnly))
        {
            if (!c.Analysed) { sb.Append($"- {c.Name}: not checked yet\n"); continue; }
            var bits = new List<string>();
            string unit = c.IsRecycleBin ? (c.Files == 1 ? "item" : "items") : (c.Files == 1 ? "file" : "files");
            bits.Add(c.Files > 0 ? $"{B(c.Bytes)} in {N(c.Files)} {unit}" : "nothing to clean");
            bits.Add(c.DefaultOn ? "on by default" : "off by default");
            if (c.AdminOnly) bits.Add(d.IsAdmin ? "needs administrator rights (available)" : "needs administrator rights");
            else if (c.PartAdmin && !d.IsAdmin) bits.Add("part needs administrator rights");
            if (c.Files > 0 && !c.Cleanable) bits.Add("can't be cleaned right now");
            AppendWrapped(sb, $"- {c.Name}: {string.Join(", ", bits)}");
            foreach (var n in c.Notes.Take(MaxNotes)) AppendWrapped(sb, "  - " + R(n), "    ");
            if (c.Notes.Count > MaxNotes) sb.Append($"  - and {c.Notes.Count - MaxNotes} more notes\n");
        }

        // ---- report-only folders
        var others = d.Categories.Where(c => c.ReportOnly).ToList();
        if (others.Count > 0)
        {
            sb.Append("\nOther space (WinScope reports these but never bulk-deletes them):\n");
            foreach (var c in others)
            {
                string v = !c.Analysed ? "not measured yet" : !c.Present ? "not present" : $"{B(c.Bytes)} in {N(c.Files)} files";
                AppendWrapped(sb, $"- {c.Name}: {v}");
                foreach (var n in c.Notes.Take(2)) AppendWrapped(sb, "  - " + R(n), "    ");
            }
        }

        // ---- last scan
        sb.Append('\n');
        if (d.Scan is ReportScan s)
        {
            string head = $"Last space scan: {PD(s.RootPath)} at {Time(s.FinishedUtc, nowUtc)} (took {Format.Span(s.Took)})" +
                          (s.Cancelled ? " – stopped early, so figures are incomplete" : "");
            AppendWrapped(sb, head + ":");
            sb.Append($"- {B(s.Bytes)} in {N(s.Files)} files and {N(s.Dirs)} folders\n");
            if (s.DriveUsed is long used && used > s.Bytes)
                AppendWrapped(sb, $"- {B(used - s.Bytes)} of the drive's used space wasn't seen by the scan (system files, " +
                                  "page/hibernation files, restore points, folders it couldn't read)");
            if (s.Unreadable > 0)
                sb.Append($"- {N(s.Unreadable)} folders couldn't be read{(d.IsAdmin ? "" : " (they need administrator rights)")}\n");
            if (s.Links > 0) sb.Append($"- {N(s.Links)} links (junctions/symbolic links) were not followed\n");
            if (s.CloudFiles > 0) sb.Append($"- {N(s.CloudFiles)} online-only cloud files ({B(s.CloudBytes)}) not counted – they aren't on this disk\n");
            if (s.TopFolders.Count > 0)
            {
                sb.Append($"Largest folders directly inside {PD(s.RootPath)}:\n");
                foreach (var f in s.TopFolders.Take(MaxFolders))
                    AppendWrapped(sb, f.IsLink
                        ? $"- {PD(f.Path)} (link, not followed)"
                        : $"- {B(f.Bytes)}  {PD(f.Path)} ({N(f.Files)} files)");
                if (s.TotalFolders > Math.Min(MaxFolders, s.TopFolders.Count))
                    sb.Append($"- and {N(s.TotalFolders - Math.Min(MaxFolders, s.TopFolders.Count))} more folders\n");
            }
            if (s.TopFiles.Count > 0)
            {
                sb.Append("Largest files:\n");
                foreach (var f in s.TopFiles.Take(MaxFiles))
                    AppendWrapped(sb, $"- {B(f.Bytes)}  {PF(f.Path)} (modified {Date(f.WriteUtc)})");
            }
        }
        else sb.Append(d.ScanRunning ? "Space scan: one is running now; no finished scan yet.\n" : "Space scan: none run this session.\n");

        // ---- cleanups this session
        sb.Append('\n');
        if (d.CleanRuns.Count == 0) sb.Append("Cleanups this session: none.\n");
        else
        {
            sb.Append("Cleanups this session (newest first):\n");
            foreach (var run in d.CleanRuns.OrderByDescending(r => r.WhenUtc).Take(MaxRuns))
            {
                long files = run.Items.Sum(i => i.Files), bytes = run.Items.Sum(i => i.Bytes);
                bool cancelled = run.Items.Any(i => i.Cancelled);
                sb.Append($"- {Time(run.WhenUtc, nowUtc)}: freed {B(bytes)} ({N(files)} files){(cancelled ? ", cancelled part-way" : "")}\n");
                foreach (var i in run.Items)
                {
                    var p = new List<string> { $"{B(i.Bytes)}, {N(i.Files)} files" };
                    if (i.PendingFiles > 0) p.Add($"{N(i.PendingFiles)} files ({B(i.PendingBytes)}) freed when the app using them closes");
                    if (i.Skipped > 0) p.Add($"skipped {N(i.Skipped)}" + (i.SkipWhy.Length > 0 ? $" ({i.SkipWhy})" : ""));
                    if (i.StoppedForGrowth) p.Add("stopped: much more than previewed");
                    AppendWrapped(sb, $"  - {i.Category}: {string.Join("; ", p)}", "    ");
                }
            }
            if (d.CleanRuns.Count > MaxRuns) sb.Append($"- and {d.CleanRuns.Count - MaxRuns} earlier cleanups\n");
        }

        // ---- what's missing
        var missing = new List<string>();
        if (!d.IsAdmin) missing.Add("Not running as administrator, so Windows' own temp folder, system crash dumps, system error " +
                                     "reports and the Windows Update cache can't be checked or cleaned.");
        if (!d.IsDesktopUser) missing.Add("WinScope is running as a different account from the signed-in person, so personal " +
                                          "folders (temp files, browser caches, Recycle Bin, Downloads) were left alone.");
        if (missing.Count > 0)
        {
            sb.Append('\n');
            foreach (var m in missing) AppendWrapped(sb, "Note: " + m, "  ");
        }

        string body = sb.ToString();
        return (findings.Select(R).ToList(), redacting ? redact(body) : body);
    }

    /// <summary>At most 5, most serious first: nearly-full drives, then big wins, then context.</summary>
    internal static List<string> Findings(StorageReportData d)
    {
        var f = new List<(int rank, string text)>();
        foreach (var dr in d.Drives.Where(x => !x.Removable && x.Total > 0))
        {
            double used = dr.Used / (double)dr.Total;
            if (used >= 0.95 || dr.Free < 5 * GB)
                f.Add((0, $"{dr.Letter} is almost full: {Pct(dr.Used, dr.Total)} used, only {B(dr.Free)} free of {B(dr.Total)}"));
            else if (used >= 0.90 || dr.Free < 15 * GB)
                f.Add((1, $"{dr.Letter} is getting full: {Pct(dr.Used, dr.Total)} used, {B(dr.Free)} free of {B(dr.Total)}"));
        }
        if (d.SafeBytes is long safe && safe >= GB)
            f.Add((2, $"About {B(safe)} of temporary files and caches can be cleaned safely (WinScope › Storage › Cleanup)"));
        var wold = d.Categories.FirstOrDefault(c => c.ReportOnly && c.Name.Contains("Windows", StringComparison.OrdinalIgnoreCase));
        if (wold is { Analysed: true, Present: true } && wold.Bytes >= GB)
            f.Add((3, $"Windows.old (previous Windows install) uses {B(wold.Bytes)}; remove via Settings › Storage › Temporary files"));
        var bin = d.Categories.FirstOrDefault(c => c.IsRecycleBin);
        if (bin is { Analysed: true } && bin.Bytes >= 5 * GB) f.Add((4, $"The Recycle Bin holds {B(bin.Bytes)} ({N(bin.Files)} items)"));
        return f.OrderBy(x => x.rank).Take(5).Select(x => x.text).ToList();
    }

    // ------------------------------------------------------------------ formatting helpers

    private static string B(long bytes) => Format.Bytes(bytes);
    private static string N(long n) => n.ToString("N0", C);

    private static string Pct(long part, long total) =>
        total <= 0 ? "—" : (part * 100.0 / total).ToString("0", C) + "%";

    /// <summary>"HH:mm" today, "ddd HH:mm" this week, "d MMM HH:mm" older (local time).</summary>
    public static string Time(DateTime utc, DateTime nowUtc)
    {
        var t = utc.ToLocalTime();
        var now = nowUtc.ToLocalTime();
        if (t.Date == now.Date) return t.ToString("HH:mm", C);
        if ((now.Date - t.Date).TotalDays < 7) return t.ToString("ddd HH:mm", C);
        return t.ToString("d MMM HH:mm", C);
    }

    private static string Date(DateTime utc) =>
        utc == DateTime.MinValue ? "unknown" : utc.ToLocalTime().ToString("d MMM yyyy", C);

    private static string Clip(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>Keep paths readable inside the line limit: "C:\Users\…\folder\file.iso".</summary>
    public static string ShortPath(string p, int max = 72)
    {
        if (p.Length <= max) return p;
        int head = Math.Min(24, max / 3);
        int tail = max - head - 1;
        return p[..head] + "…" + p[^tail..];
    }

    /// <summary>Word-wrap one logical line at <see cref="MaxLine"/>, continuing with <paramref name="indent"/>.</summary>
    public static void AppendWrapped(StringBuilder sb, string line, string indent = "  ")
    {
        while (line.Length > MaxLine)
        {
            int cut = line.LastIndexOf(' ', MaxLine);
            if (cut <= indent.Length + 10) cut = MaxLine;
            sb.Append(line[..cut].TrimEnd()).Append('\n');
            line = indent + line[cut..].TrimStart();
        }
        sb.Append(line).Append('\n');
    }
}

/// <summary>
/// Makes scan paths safe to share. Call on text that has ALREADY been through Redactor.Apply (which maps your
/// profile to %USERPROFILE% etc.); shorten afterwards. System and app locations keep their full path; elsewhere
/// personal names are hidden but the shape stays ("%USERPROFILE%\Videos\…\(name hidden).mp4"), so a reader still
/// learns "big videos in Videos" without seeing titles. Account folder names under \Users become [user].
/// </summary>
public static class ReportPaths
{
    public const string HiddenName = "(name hidden)", HiddenFolder = "(folder name hidden)", User = "[user]";

    /// <summary>Directly under a drive: everything below these is Windows/app/game data and is kept as is.</summary>
    private static readonly HashSet<string> TopAnchors = new(StringComparer.OrdinalIgnoreCase)
    {
        "Windows", "Windows.old", "Program Files", "Program Files (x86)", "ProgramData", "$Recycle.Bin", "Recovery",
        "System Volume Information", "$WinREAgent", "$Windows.~BT", "$Windows.~WS", "PerfLogs", "Steam", "XboxGames",
        "SteamLibrary", "Epic Games", "GOG Games", "Riot Games", "Origin Games", "EA Games", "Ubisoft", "Battle.net",
        "WindowsApps", "Intel", "AMD", "NVIDIA",
    };

    /// <summary>Anywhere in a path: app data and game libraries.</summary>
    private static readonly HashSet<string> Anchors = new(StringComparer.OrdinalIgnoreCase)
    {
        "AppData", "steamapps", "SteamLibrary", "Epic Games", "XboxGames", "GOG Games", "GOG Galaxy", "Riot Games",
        "Origin Games", "EA Games", "Ubisoft Game Launcher", "Battle.net", "WindowsApps", "ModifiableWindowsApps",
    };

    /// <summary>Root tokens Redactor.Apply produces that are app data (kept in full).</summary>
    private static readonly HashSet<string> AnchorRoots = new(StringComparer.OrdinalIgnoreCase) { "%LOCALAPPDATA%", "%APPDATA%" };

    /// <summary>Folder names that say what's there without saying anything personal.</summary>
    private static readonly HashSet<string> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        "Users", User, "%USERPROFILE%", "%OneDrive%", "Public", "Default", "Documents and Settings", "Documents", "My Documents", "Downloads", "Desktop",
        "Pictures", "Videos", "Music", "OneDrive", "Local", "Roaming", "LocalLow", "Saved Games", "Favorites", "Links",
        "Contacts", "Searches", "3D Objects", "Camera Roll", "Screenshots", "Saved Pictures", "Captures", "Recordings",
        "Temp", "Games", "Backup", "Backups", "source", "repos", "Projects", "Dropbox", "Google Drive", "iCloudDrive",
        ".cache", "node_modules", ".nuget", "packages", ".vscode", ".gradle", ".m2", ".npm", ".cargo", ".rustup", ".docker",
        ".android", ".conda", "anaconda3", "miniconda3", ".ollama", ".lmstudio", "models", "venv", ".venv",
    };

    /// <summary>Account folders under \Users that aren't a person.</summary>
    private static readonly HashSet<string> SharedProfiles = new(StringComparer.OrdinalIgnoreCase)
    { "Public", "Default", "Default User", "All Users", User };

    private static readonly string[] SystemFileNames = { "pagefile.sys", "hiberfil.sys", "swapfile.sys", "MEMORY.DMP", "ext4.vhdx" };
    private static readonly string[] SystemExtensions = { ".dmp", ".vhdx", ".vhd", ".wim", ".esd" };

    /// <summary>
    /// Under AppData, folder names (apps) are kept but file names only for system/app file types – app data can
    /// hold personal media (chat app transfers, downloads, files named after accounts).
    /// </summary>
    private static readonly string[] AppFileExtensions =
    {
        ".dmp", ".vhdx", ".vhd", ".log", ".etl", ".db", ".sqlite", ".ldb", ".pak", ".bin", ".dat", ".tmp", ".cache",
        ".msi", ".msp", ".cab", ".exe", ".dll", ".wim", ".esd",
    };

    public static bool IsAppFileType(string name) =>
        AppFileExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    public static string File(string redactedPath) => Sanitise(redactedPath, isFile: true);
    public static string Folder(string redactedPath) => Sanitise(redactedPath, isFile: false);

    private static string Sanitise(string path, bool isFile)
    {
        if (string.IsNullOrEmpty(path)) return path;
        // Redactor.Apply replaces the LocalAppData path as a plain substring, so "…\AppData\LocalLow" comes out
        // as "%LOCALAPPDATA%Low" – put it back into a recognisable form.
        if (path.StartsWith("%LOCALAPPDATA%Low", StringComparison.OrdinalIgnoreCase))
            path = @"%USERPROFILE%\AppData\LocalLow" + path["%LOCALAPPDATA%Low".Length..];
        var segs = path.Replace('/', '\\').Split('\\').ToList();
        bool unc = path.StartsWith(@"\\", StringComparison.Ordinal);
        if (unc) return isFile ? @"\\(network path)\" + HiddenFileName(segs[^1]) : @"\\(network path)";
        if (segs.Count > 1 && segs[^1].Length == 0) segs.RemoveAt(segs.Count - 1);       // trailing separator
        string root = segs[0];
        bool driveRoot = root.Length == 2 && root[1] == ':';
        var outp = new List<string> { root };
        bool keepAll = AnchorRoots.Contains(root);
        bool appData = keepAll;              // under AppData: keep folder names, but only app/system file names
        bool gap = false;                    // hidden segments pending (shown as one "…")
        int last = isFile ? segs.Count - 1 : segs.Count;
        bool afterRecycle = false;

        for (int i = 1; i < last; i++)
        {
            string s = segs[i];
            bool isLastFolder = !isFile && i == segs.Count - 1;
            if (afterRecycle) { afterRecycle = false; if (s.StartsWith("S-1-", StringComparison.OrdinalIgnoreCase)) { outp.Add(User); continue; } }
            if (driveRoot && i == 2 && string.Equals(segs[1], "Users", StringComparison.OrdinalIgnoreCase))
            {
                FlushGap();
                outp.Add(SharedProfiles.Contains(s) ? s : User);            // other accounts' names never shown
                continue;
            }
            if (keepAll) { outp.Add(s); continue; }
            if ((driveRoot && i == 1 && TopAnchors.Contains(s)) || Anchors.Contains(s))
            {
                FlushGap();
                outp.Add(s);
                keepAll = true;
                appData = string.Equals(s, "AppData", StringComparison.OrdinalIgnoreCase);
                afterRecycle = string.Equals(s, "$Recycle.Bin", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (Known.Contains(s)) { FlushGap(); outp.Add(s); continue; }
            if (s.StartsWith("OneDrive", StringComparison.OrdinalIgnoreCase)) { FlushGap(); outp.Add("OneDrive"); continue; }   // "OneDrive - Company"
            if (isLastFolder) { FlushGap(); outp.Add(HiddenFolder); continue; }
            gap = true;
        }
        if (!isFile) { FlushGap(); return Join(outp); }

        string name = segs.Count > 1 ? segs[^1] : "";
        bool keepName = appData ? IsSystemFile(name) || IsAppFileType(name) : keepAll || IsSystemFile(name);
        if (keepName) { FlushGap(); outp.Add(name); }
        else { FlushGap(); outp.Add(HiddenFileName(name)); }
        return Join(outp);

        void FlushGap() { if (gap) { outp.Add("…"); gap = false; } }
    }

    private static string Join(List<string> segs) => segs.Count == 1 && segs[0].EndsWith(':') ? segs[0] + "\\" : string.Join('\\', segs);

    public static bool IsSystemFile(string name) =>
        SystemFileNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)) ||
        SystemExtensions.Any(e => name.EndsWith(e, StringComparison.OrdinalIgnoreCase));

    /// <summary>"(name hidden).mp4" – the extension kept when it's a short plain one.</summary>
    public static string HiddenFileName(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1) return HiddenName;
        string ext = name[dot..];
        bool plain = ext.Length <= 9 && ext.Skip(1).All(char.IsAsciiLetterOrDigit);
        return plain ? HiddenName + ext.ToLowerInvariant() : HiddenName;
    }
}
