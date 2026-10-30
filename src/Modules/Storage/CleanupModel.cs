using System.Diagnostics;
using WinScope.Core;

namespace WinScope.Modules.Storage;

internal enum CatKind { Files, RecycleBin, ReportWindowsOld, ReportDownloads }

/// <summary>One folder a category cleans, with its filter.</summary>
internal sealed class CleanRoot
{
    public required string Path { get; init; }
    /// <summary>
    /// The folder this root must really sit inside, with no link between the two (e.g. %LOCALAPPDATA%). If the
    /// root's real location isn't under the parent's real location, the whole category is skipped.
    /// </summary>
    public required string ExpectedParent { get; init; }
    /// <summary>Simple wildcard on the file name; null = every file.</summary>
    public string? Pattern { get; init; }
    public bool Recurse { get; init; } = true;
    /// <summary>Only files (and folders) whose newest of created/modified/changed is at least this old.</summary>
    public TimeSpan? MinAge { get; init; }
    public bool NeedsAdmin { get; init; }
    /// <summary>Belongs to the signed-in person (left alone when WinScope runs as a different account).</summary>
    public bool PerUser { get; init; }
    /// <summary>The app that must be closed first (e.g. "Edge") and its process names.</summary>
    public string? App { get; init; }
    public string[] Processes { get; init; } = Array.Empty<string>();
    /// <summary>The user's %TEMP%/%TMP%: only %LOCALAPPDATA%\Temp (or Temp\N) is accepted.</summary>
    public bool TempRoot { get; init; }
}

internal sealed class CategoryDef
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public string? Caution { get; init; }
    public bool DefaultOn { get; init; }
    public CatKind Kind { get; init; } = CatKind.Files;
    /// <summary>Every root needs admin (shows a lock when not elevated).</summary>
    public bool AdminOnly { get; init; }
    /// <summary>Some roots need admin.</summary>
    public bool PartAdmin { get; init; }
    /// <summary>The whole category is the signed-in person's (Recycle Bin, Downloads).</summary>
    public bool PerUser { get; init; }
    /// <summary>Roots are worked out fresh at analysis time; a clean only touches the roots that were confirmed.</summary>
    public Func<List<CleanRoot>> Roots { get; init; } = () => new List<CleanRoot>();
    /// <summary>If any of these processes is running (any session), skip the whole category.</summary>
    public string[] BlockProcesses { get; init; } = Array.Empty<string>();
    /// <summary>Only clean when all of these services are stopped.</summary>
    public string[] RequireStoppedServices { get; init; } = Array.Empty<string>();
    public string? BlockMessage { get; init; }
    public bool ReportOnly => Kind is CatKind.ReportWindowsOld or CatKind.ReportDownloads;
}

internal enum RootState { Ready, Missing, NeedsAdmin, AppRunning, Busy, Refused, Denied, OtherAccount, Redirected }

internal sealed class RootStatus
{
    public required string Path { get; init; }
    public RootState State { get; init; }
    public long Files { get; init; }
    public long Bytes { get; init; }
    public string? Why { get; init; }
    public string? App { get; init; }
}

internal sealed class CategoryResult
{
    public required CategoryDef Def { get; init; }
    public List<RootStatus> Roots { get; init; } = new();
    /// <summary>Files/bytes that can be cleaned right now (ready roots only).</summary>
    public long Files { get; init; }
    public long Bytes { get; init; }
    /// <summary>Largest files in the ready roots (preview).</summary>
    public List<BigFile> Top { get; init; } = new();
    public List<string> Notes { get; init; } = new();
    /// <summary>For report-only categories: the folder and whether it exists.</summary>
    public string? Folder { get; init; }
    public bool Present { get; init; } = true;
    public bool Cleanable => !Def.ReportOnly && Files > 0;
    /// <summary>The folders a clean of this category would touch (ready roots).</summary>
    public IEnumerable<string> ReadyRoots => Roots.Where(r => r.State == RootState.Ready).Select(r => r.Path);
}

/// <summary>What the user confirmed for one category: the clean never goes beyond it.</summary>
internal sealed record CleanRequest(CategoryDef Def, long PreviewFiles, long PreviewBytes, IReadOnlyList<string> RootPaths);

internal sealed class CleanOutcome
{
    public required CategoryDef Def { get; init; }
    public long Files, Bytes, Dirs;
    /// <summary>Marked for deletion but another app still has them open: freed when it closes them.</summary>
    public long PendingFiles, PendingBytes;
    public SkipCounts Skipped { get; } = new();
    public List<string> Notes { get; } = new();
    public bool Cancelled, StoppedForGrowth;
}

internal sealed class EngineSnapshot
{
    public IReadOnlyDictionary<string, CategoryResult> Results { get; init; } = new Dictionary<string, CategoryResult>();
    public bool Analysing { get; init; }
    public bool ReportsAnalysing { get; init; }
    public bool Cleaning { get; init; }
    public DateTime? AnalysedUtc { get; init; }
    /// <summary>Default-on categories that can be cleaned at the current privilege level; null until analysed.</summary>
    public long? SafeBytes { get; init; }
}

internal sealed class CleanProgress
{
    public string Category = "";
    public long Files, Bytes, Skipped;
}

/// <summary>A cleanup root after checking it on disk: where it is as given, where it really is, and its identity.</summary>
internal sealed record ResolvedRoot(string Raw, string Norm, string Final, FileId Id);

/// <summary>Folders WinScope treats as off-limits or critical. Worked out once.</summary>
internal static class SafetyContext
{
    private static readonly Lazy<IReadOnlyList<string>> _protected = new(BuildProtected);
    private static readonly Lazy<IReadOnlyList<string>> _critical = new(BuildCritical);
    private static readonly Lazy<IReadOnlyList<string>> _oneDrive = new(BuildOneDrive);
    private static readonly Lazy<(string? norm, string? final)> _localTemp = new(() =>
    {
        string local = K(Environment.SpecialFolder.LocalApplicationData);
        if (local.Length == 0) return (null, null);
        string t = Path.Combine(local, "Temp");
        string full = SafeFull(t);
        return (WinPath.Normalize(full.Length > 0 && OperatingSystem.IsWindows() ? StorageNative.ExpandShortNames(full) : full), FinalOf(t));
    });

    /// <summary>Contents never go to the Recycle Bin from the analyser.</summary>
    public static IReadOnlyList<string> Protected => _protected.Value;
    /// <summary>A cleanup root may never be one of these (or contain one).</summary>
    public static IReadOnlyList<string> Critical => _critical.Value;
    /// <summary>OneDrive folders (as given and where they really are).</summary>
    public static IReadOnlyList<string> OneDrive => _oneDrive.Value;
    /// <summary>%LOCALAPPDATA%\Temp normalised as given / where it really is.</summary>
    public static (string? norm, string? final) LocalTemp => _localTemp.Value;

    private static string K(Environment.SpecialFolder f)
    {
        try { return Environment.GetFolderPath(f, Environment.SpecialFolderOption.DoNotVerify); } catch { return ""; }
    }

    private static string SafeFull(string p)
    {
        try { return Path.GetFullPath(p); } catch { return ""; }
    }

    /// <summary>Where a folder really lives (normalised), or null.</summary>
    public static string? FinalOf(string path)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            string full = Path.GetFullPath(path);
            using var h = StorageNative.Open(full, forDelete: false, listDirectory: false, out _);
            return h.IsInvalid ? null : StorageNative.FinalPath(h);
        }
        catch { return null; }
    }

    private static IReadOnlyList<string> BuildProtected()
    {
        var raw = new List<string>
        {
            K(Environment.SpecialFolder.Windows),
            K(Environment.SpecialFolder.ProgramFiles),
            K(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
            Environment.GetEnvironmentVariable("SystemRoot") ?? "",
        };
        string pd = K(Environment.SpecialFolder.CommonApplicationData);
        if (pd.Length > 0) raw.Add(Path.Combine(pd, "Microsoft"));
        return WithFinalPaths(raw);
    }

    private static IReadOnlyList<string> BuildCritical()
    {
        var raw = new List<string>
        {
            K(Environment.SpecialFolder.Windows), K(Environment.SpecialFolder.System), K(Environment.SpecialFolder.SystemX86),
            K(Environment.SpecialFolder.ProgramFiles), K(Environment.SpecialFolder.ProgramFilesX86),
            K(Environment.SpecialFolder.CommonApplicationData), K(Environment.SpecialFolder.UserProfile),
            K(Environment.SpecialFolder.LocalApplicationData), K(Environment.SpecialFolder.ApplicationData),
            K(Environment.SpecialFolder.Desktop), K(Environment.SpecialFolder.MyDocuments), K(Environment.SpecialFolder.MyPictures),
            K(Environment.SpecialFolder.MyMusic), K(Environment.SpecialFolder.MyVideos), K(Environment.SpecialFolder.CommonDocuments),
            Environment.GetEnvironmentVariable("OneDrive") ?? "", Environment.GetEnvironmentVariable("ProgramW6432") ?? "",
        };
        string pd = K(Environment.SpecialFolder.CommonApplicationData);
        if (pd.Length > 0) raw.Add(Path.Combine(pd, "Microsoft"));
        string profile = K(Environment.SpecialFolder.UserProfile);
        if (profile.Length > 0 && Path.GetDirectoryName(profile) is string users) raw.Add(users);
        try { if (StorageNative.DownloadsFolder() is string dl) raw.Add(dl); } catch { }
        return WithFinalPaths(raw);
    }

    private static IReadOnlyList<string> BuildOneDrive() =>
        WithFinalPaths(new List<string>
        {
            Environment.GetEnvironmentVariable("OneDrive") ?? "",
            Environment.GetEnvironmentVariable("OneDriveConsumer") ?? "",
            Environment.GetEnvironmentVariable("OneDriveCommercial") ?? "",
        });

    /// <summary>Normalised paths plus, where it differs, where each really lives (junction-moved folders).</summary>
    private static IReadOnlyList<string> WithFinalPaths(List<string> raw)
    {
        var set = new List<string>();
        void Add(string? n) { if (n != null && !WinPath.IsRoot(n) && !set.Any(x => WinPath.SamePath(x, n))) set.Add(n); }
        foreach (var r in raw)
        {
            if (string.IsNullOrWhiteSpace(r) || r.Contains('%')) continue;
            string full = SafeFull(r);
            Add(WinPath.Normalize(full));
            if (full.Length > 0) Add(FinalOf(full));
        }
        return set;
    }
}

internal static class ProcessCheck
{
    /// <summary>Whether any process with one of these names runs (in our session only, unless <paramref name="anySession"/>).</summary>
    public static bool AnyRunning(string[] names, bool anySession)
    {
        int session;
        try { using var me = Process.GetCurrentProcess(); session = me.SessionId; } catch { session = -1; }
        foreach (var n in names)
        {
            Process[] ps;
            try { ps = Process.GetProcessesByName(n); } catch { continue; }
            try
            {
                foreach (var p in ps)
                {
                    try { if (anySession || session < 0 || p.SessionId == session) return true; }
                    catch { return true; }           // can't tell → assume it's running (safer)
                }
            }
            finally { foreach (var p in ps) p.Dispose(); }
        }
        return false;
    }

    /// <summary>True only if every service is definitely stopped.</summary>
    public static bool AllServicesStopped(string[] names) => names.All(StorageNative.IsServiceStopped);
}

/// <summary>Where a different account is running WinScope, per-user things aren't ours to touch.</summary>
internal static class AccountGuard
{
    public static bool PersonalAllowed => Elevation.IsDesktopUser;

    public static string Note =>
        $"WinScope is running as {(string.IsNullOrEmpty(Elevation.AccountName) ? "a different account" : Elevation.AccountName)}, " +
        "not as the person signed in, so it leaves personal folders alone. Restart WinScope normally to use this.";
}
