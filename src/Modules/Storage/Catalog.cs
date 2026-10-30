using WinScope.Core;

namespace WinScope.Modules.Storage;

/// <summary>The cleanup categories. Roots are worked out each time they're used (so new browser profiles appear).</summary>
internal static class Catalog
{
    private static readonly TimeSpan OneDay = TimeSpan.FromHours(24);

    private static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
    private static string Win => Environment.GetFolderPath(Environment.SpecialFolder.Windows, Environment.SpecialFolderOption.DoNotVerify);
    private static string ProgData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolderOption.DoNotVerify);

    /// <summary>"C:\Windows.old" on the Windows drive, or null.</summary>
    public static string? WindowsOldPath()
    {
        string? root = string.IsNullOrEmpty(Win) ? null : Path.GetPathRoot(Win);
        return string.IsNullOrEmpty(root) ? null : Path.Combine(root, "Windows.old");
    }

    public static List<CategoryDef> Build() => new()
    {
        new CategoryDef
        {
            Id = "usertemp", Name = "Your temporary files", DefaultOn = true,
            Description = "Leftovers apps put in your Temp folder (%LOCALAPPDATA%\\Temp). Only files and folders nobody has touched for over a day.",
            Roots = UserTempRoots,
        },
        new CategoryDef
        {
            Id = "wintemp", Name = "Windows temporary files", DefaultOn = Elevation.IsAdmin, AdminOnly = true,
            Description = "Windows' own Temp folder. Only files older than a day.",
            Roots = () => Many(Root(Combine(Win, "Temp"), Win, minAge: OneDay, admin: true)),
        },
        new CategoryDef
        {
            Id = "recycle", Name = "Recycle Bin", Kind = CatKind.RecycleBin, DefaultOn = false, PerUser = true,
            Description = "Files you've already deleted. Once it's emptied they can't be restored.",
            Caution = "Off by default in case there's something in there you want back.",
        },
        new CategoryDef
        {
            Id = "browsers", Name = "Browser caches", DefaultOn = true,
            Description = "Copies of web pages and images that Chrome, Edge, Brave and Firefox keep to load sites faster. " +
                          "Only the cache folders – your logins, cookies, history and bookmarks aren't touched.",
            Roots = BrowserRoots,
        },
        new CategoryDef
        {
            Id = "wer", Name = "Windows Error Reporting files", DefaultOn = true, PartAdmin = true,
            Description = "Reports Windows saved after apps crashed or stopped responding.",
            Roots = () => Many(
                Root(Combine(Local, @"Microsoft\Windows\WER\ReportArchive"), Local, perUser: true),
                Root(Combine(Local, @"Microsoft\Windows\WER\ReportQueue"), Local, perUser: true),
                Root(Combine(ProgData, @"Microsoft\Windows\WER\ReportArchive"), ProgData, admin: true),
                Root(Combine(ProgData, @"Microsoft\Windows\WER\ReportQueue"), ProgData, admin: true)),
        },
        new CategoryDef
        {
            Id = "crashdumps", Name = "App crash dumps", DefaultOn = true,
            Description = "Memory snapshots saved when apps crashed. Only useful to developers.",
            Roots = () => Many(Root(Combine(Local, "CrashDumps"), Local, pattern: "*.dmp", perUser: true)),
        },
        new CategoryDef
        {
            Id = "sysdumps", Name = "System crash dumps", DefaultOn = false, AdminOnly = true,
            Description = "Memory snapshots Windows saved after blue screens.",
            Caution = "Keep these if you're troubleshooting blue screens.",
            Roots = () => Many(
                Root(Combine(Win, "Minidump"), Win, pattern: "*.dmp", recurse: false, admin: true),
                Root(Win, DriveRoot(Win), pattern: "MEMORY.DMP", recurse: false, admin: true)),
        },
        new CategoryDef
        {
            Id = "thumbs", Name = "Thumbnail cache", DefaultOn = true,
            Description = "Picture previews Explorer keeps. They're rebuilt as you browse; files Explorer is using are skipped.",
            Roots = () => Many(Root(Combine(Local, @"Microsoft\Windows\Explorer"), Local, pattern: "thumbcache_*.db", recurse: false, perUser: true)),
        },
        new CategoryDef
        {
            Id = "shaders", Name = "Shader caches", DefaultOn = false,
            Description = "Graphics shaders that games and apps have compiled for your graphics card.",
            Caution = "Games rebuild these; the first launch afterwards may stutter.",
            Roots = () => Many(
                Root(Combine(Local, "D3DSCache"), Local, perUser: true),
                Root(Combine(Local, @"NVIDIA\DXCache"), Local, perUser: true),
                Root(Combine(Local, @"NVIDIA\GLCache"), Local, perUser: true),
                Root(Combine(Local, @"AMD\DxCache"), Local, perUser: true)),
        },
        new CategoryDef
        {
            Id = "wu", Name = "Windows Update download cache", DefaultOn = false, AdminOnly = true,
            Description = "Update files Windows has already downloaded. It fetches them again if it needs them.",
            Caution = "Only cleaned while Windows Update and BITS are stopped and no update is installing. Files Windows is using are skipped.",
            BlockProcesses = new[] { "TiWorker", "TrustedInstaller" },
            RequireStoppedServices = new[] { "wuauserv", "bits" },
            BlockMessage = "Windows is installing updates right now – try again once it's finished.",
            Roots = () => Many(Root(Combine(Win, @"SoftwareDistribution\Download"), Win, admin: true)),
        },
        new CategoryDef
        {
            Id = "windowsold", Name = "Previous Windows installation", Kind = CatKind.ReportWindowsOld,
            Description = "Windows.old holds your old Windows after an upgrade, so you can go back. WinScope doesn't delete it – " +
                          "remove it via Settings › System › Storage › Temporary files.",
        },
        new CategoryDef
        {
            Id = "downloads", Name = "Downloads folder", Kind = CatKind.ReportDownloads, PerUser = true,
            Description = "Your own files, so WinScope never bulk-deletes them. Review the largest and move any you don't need " +
                          "to the Recycle Bin one at a time.",
        },
    };

    private static List<CleanRoot> Many(params CleanRoot?[] roots) => roots.Where(r => r != null).Cast<CleanRoot>().ToList();

    private static string Combine(string a, string b) => string.IsNullOrEmpty(a) ? "" : Path.Combine(a, b);

    private static string DriveRoot(string p) => string.IsNullOrEmpty(p) ? "" : Path.GetPathRoot(p) ?? "";

    private static CleanRoot? Root(string path, string parent, string? pattern = null, bool recurse = true, TimeSpan? minAge = null,
        bool admin = false, bool perUser = false, string? app = null, string[]? processes = null, bool temp = false) =>
        string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parent) ? null : new CleanRoot
        {
            Path = path, ExpectedParent = parent, Pattern = pattern, Recurse = recurse, MinAge = minAge, NeedsAdmin = admin,
            PerUser = perUser, App = app, Processes = processes ?? Array.Empty<string>(), TempRoot = temp,
        };

    /// <summary>
    /// %TEMP% (and %TMP% if it points somewhere else), files older than a day. Only %LOCALAPPDATA%\Temp (or a
    /// numbered session folder in it) passes <see cref="CleanupEngine.Resolve"/>; anything else shows as "not offered".
    /// </summary>
    private static List<CleanRoot> UserTempRoots()
    {
        var list = new List<CleanRoot>();
        var seen = new List<string>();
        foreach (var v in new[] { "TEMP", "TMP" })
        {
            string? p = Environment.GetEnvironmentVariable(v);
            if (string.IsNullOrWhiteSpace(p)) continue;
            string key = p.Trim();
            try
            {
                if (!key.Contains('%') && Path.IsPathFullyQualified(key))
                    key = WinPath.Normalize(StorageNative.ExpandShortNames(Path.GetFullPath(key))) ?? key;
            }
            catch { }
            if (seen.Any(s => string.Equals(s, key, StringComparison.OrdinalIgnoreCase))) continue;
            seen.Add(key);
            if (Root(p, Local, minAge: OneDay, perUser: true, temp: true) is CleanRoot cr) list.Add(cr);
        }
        return list;
    }

    private static readonly (string app, string process, string userData)[] Chromium =
    {
        ("Chrome", "chrome", @"Google\Chrome\User Data"),
        ("Edge", "msedge", @"Microsoft\Edge\User Data"),
        ("Brave", "brave", @"BraveSoftware\Brave-Browser\User Data"),
    };

    private static readonly string[] ChromiumCaches = { "Cache", "Code Cache", "GPUCache" };

    /// <summary>Only the cache folders inside each real (non-link) profile folder.</summary>
    private static List<CleanRoot> BrowserRoots()
    {
        var list = new List<CleanRoot>();
        var buf = new List<DirEntry>();
        string local = Local;
        if (string.IsNullOrEmpty(local)) return list;

        foreach (var (app, proc, rel) in Chromium)
        {
            string ud = Path.Combine(local, rel);
            if (!Directory.Exists(ud) || FsWalk.List(ud, buf, long.MaxValue) != ListStatus.Ok) continue;
            foreach (var e in buf.ToList())
            {
                if (!e.IsDirectory || e.Name == null || FileRules.IsLink(e.Attributes) || !FileRules.IsChromiumProfileName(e.Name)) continue;
                foreach (var c in ChromiumCaches)
                {
                    string path = Path.Combine(ud, e.Name, c);
                    if (Directory.Exists(path)) list.Add(Root(path, local, perUser: true, app: app, processes: new[] { proc })!);
                }
            }
        }

        string ff = Path.Combine(local, @"Mozilla\Firefox\Profiles");
        if (Directory.Exists(ff) && FsWalk.List(ff, buf, long.MaxValue) == ListStatus.Ok)
            foreach (var e in buf.ToList())
            {
                if (!e.IsDirectory || e.Name == null || FileRules.IsLink(e.Attributes)) continue;
                string path = Path.Combine(ff, e.Name, "cache2");
                if (Directory.Exists(path)) list.Add(Root(path, local, perUser: true, app: "Firefox", processes: new[] { "firefox" })!);
            }
        return list;
    }
}
