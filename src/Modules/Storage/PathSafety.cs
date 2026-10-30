namespace WinScope.Modules.Storage;

// Pure path logic (no WPF, no P/Invoke) so it can be unit-tested on any OS.
// Everything here treats paths as Windows paths regardless of the OS it runs on.

/// <summary>
/// Lexical Windows path canonicalisation for safety comparisons. This never touches the disk: the real
/// guard at delete time is the handle-based final-path check in <see cref="StorageNative"/>; this is the
/// first, cheap layer and is deliberately strict – anything odd returns null (= "refuse").
/// </summary>
public static class WinPath
{
    public const int MaxLength = 32767;

    /// <summary>
    /// Canonical form of a fully-qualified path, or null when the path is invalid, relative, drive-relative
    /// ("C:foo"), a device/volume-GUID path, contains wildcards/quotes/control characters, an alternate data
    /// stream (':' after the drive), or a ".." that would climb above the root.
    /// • '/' becomes '\', repeated separators collapse, "." segments drop, ".." pops.
    /// • "\\?\C:\x", "\\.\C:\x" and "\??\C:\x" become "C:\x"; "\\?\UNC\srv\sh\x" becomes "\\srv\sh\x".
    /// • Trailing dots and spaces are trimmed from every segment (Win32 ignores them); a segment that is only
    ///   dots/spaces (other than "." / "..") is rejected.
    /// • Drive letters are upper-cased; other case is kept (compare with OrdinalIgnoreCase).
    /// • Roots keep a trailing separator ("C:\", "\\srv\share\"); nothing else does.
    /// </summary>
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > MaxLength) return null;
        string p = path.Replace('/', '\\');

        // Strip the Win32/NT namespace prefixes. Only drive-letter and UNC forms are accepted after that.
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal) ||
                 p.StartsWith(@"\??\", StringComparison.Ordinal))
        {
            p = p[4..];
            if (!IsDriveRooted(p)) return null;            // \\?\Volume{…}, \\.\PhysicalDrive0, \\.\pipe\… → refuse
        }

        foreach (char c in p)
            if (c < 32 || c is '*' or '?' or '"' or '<' or '>' or '|') return null;

        string prefix;
        int restStart;
        if (IsDriveRooted(p))
        {
            prefix = char.ToUpperInvariant(p[0]) + @":\";
            restStart = 3;
        }
        else if (p.Length > 2 && p[0] == '\\' && p[1] == '\\')
        {
            int s1 = p.IndexOf('\\', 2);
            if (s1 <= 2) return null;                       // no server, or "\\\"
            string server = p[2..s1];
            int s2 = p.IndexOf('\\', s1 + 1);
            string share = s2 < 0 ? p[(s1 + 1)..] : p[(s1 + 1)..s2];
            if (!IsPlainSegment(server) || !IsPlainSegment(share)) return null;
            prefix = @"\\" + server + @"\" + share + @"\";
            restStart = s2 < 0 ? p.Length : s2 + 1;
        }
        else return null;                                   // relative, "\foo", "C:foo"

        var segs = new List<string>();
        int i = restStart;
        while (i <= p.Length)
        {
            int j = p.IndexOf('\\', i);
            if (j < 0) j = p.Length;
            string seg = p[i..j];
            i = j + 1;
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..")
            {
                if (segs.Count == 0) return null;           // would climb above the root
                segs.RemoveAt(segs.Count - 1);
                continue;
            }
            string t = seg.TrimEnd('.', ' ');
            if (t.Length == 0) return null;                 // "...", " ", ". " – ambiguous, refuse
            if (t.Contains(':')) return null;               // alternate data stream or stray drive
            segs.Add(t);
        }
        if (segs.Count == 0) return prefix;
        return prefix + string.Join('\\', segs);
    }

    private static bool IsDriveRooted(string p) =>
        p.Length >= 3 && IsAsciiLetter(p[0]) && p[1] == ':' && p[2] == '\\';

    private static bool IsAsciiLetter(char c) => c is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private static bool IsPlainSegment(string s) =>
        s.Length > 0 && s != "." && s != ".." && s.TrimEnd('.', ' ').Length == s.Length && !s.Contains(':');

    /// <summary>Length of the root part ("C:\" = 3, "\\srv\sh\" = its length) of a normalised path.</summary>
    public static int RootLength(string norm)
    {
        if (norm.Length >= 3 && norm[1] == ':') return 3;
        int s1 = norm.IndexOf('\\', 2);
        int s2 = s1 < 0 ? -1 : norm.IndexOf('\\', s1 + 1);
        return s2 < 0 ? norm.Length : s2 + 1;
    }

    /// <summary>True for "C:\" or "\\srv\share\" (normalised).</summary>
    public static bool IsRoot(string norm) => norm.Length == RootLength(norm);

    public static bool IsUnc(string norm) => norm.StartsWith(@"\\", StringComparison.Ordinal);

    /// <summary>Number of segments below the root: "C:\" = 0, "C:\a" = 1, "C:\a\b" = 2.</summary>
    public static int Depth(string norm)
    {
        int r = RootLength(norm);
        if (norm.Length == r) return 0;
        int n = 1;
        for (int k = r; k < norm.Length; k++) if (norm[k] == '\\') n++;
        return n;
    }

    /// <summary>Parent of a normalised path, or null for a root.</summary>
    public static string? Parent(string norm)
    {
        int r = RootLength(norm);
        if (norm.Length <= r) return null;
        int idx = norm.LastIndexOf('\\');
        return idx < r ? norm[..r] : norm[..idx];
    }

    /// <summary>Last segment of a normalised path ("" for a root).</summary>
    public static string Name(string norm)
    {
        int r = RootLength(norm);
        if (norm.Length <= r) return "";
        return norm[(norm.LastIndexOf('\\') + 1)..];
    }

    /// <summary>First segment below the root ("" for a root).</summary>
    public static string FirstSegment(string norm)
    {
        int r = RootLength(norm);
        if (norm.Length <= r) return "";
        int j = norm.IndexOf('\\', r);
        return j < 0 ? norm[r..] : norm[r..j];
    }

    public static bool SamePath(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True when <paramref name="child"/> is strictly below <paramref name="root"/> (both normalised).
    /// "…\Temp2\x" is not inside "…\Temp"; the root itself is not inside itself.
    /// </summary>
    public static bool IsInside(string? child, string? root)
    {
        if (child == null || root == null) return false;
        string r = root.EndsWith('\\') ? root : root + '\\';
        return child.Length > r.Length && child.StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrInside(string? child, string? root) => SamePath(child, root) || IsInside(child, root);

    /// <summary>Path as Windows gave it (e.g. GetFinalPathNameByHandleW) minus a "\\?\" / "\\?\UNC\" prefix – nothing else changed.</summary>
    public static string StripExtendedPrefix(string raw)
    {
        if (raw.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + raw[8..];
        if (raw.StartsWith(@"\\?\", StringComparison.Ordinal)) return raw[4..];
        return raw;
    }

    /// <summary>
    /// True when any component ends in '.' or ' ' (other than "." / ".."). Win32 silently trims those, so such
    /// a name can't be passed safely to path-based APIs: "movie.mkv " would act on a sibling "movie.mkv".
    /// </summary>
    public static bool HasTrailingDotOrSpaceSegment(string raw)
    {
        string p = StripExtendedPrefix(raw);
        int start = 0;
        for (int i = 0; i <= p.Length; i++)
        {
            if (i < p.Length && p[i] != '\\' && p[i] != '/') continue;
            int len = i - start;
            if (len > 0)
            {
                bool dots = len <= 2 && p.AsSpan(start, len).Trim('.').Length == 0;
                char last = p[i - 1];
                if (!dots && (last == '.' || last == ' ')) return true;
            }
            start = i + 1;
        }
        return false;
    }

    /// <summary>The first <paramref name="depth"/> segments of a normalised path (depth 0 = its root).</summary>
    public static string? PrefixAtDepth(string norm, int depth)
    {
        if (depth < 0 || depth > Depth(norm)) return null;
        string p = norm;
        for (int d = Depth(norm); d > depth; d--) p = Parent(p)!;
        return p;
    }

    /// <summary>Normalise, then require strictly inside the (normalised) root. Null/invalid → false.</summary>
    public static bool IsStrictlyInside(string? rawChild, string? normRoot) => IsInside(Normalize(rawChild), normRoot);
}

/// <summary>Rules for what WinScope will never touch, and which cleanup roots are sane.</summary>
public static class SafetyRules
{
    /// <summary>Files Windows needs at the top of a drive (or anywhere – these names are never ours to delete).</summary>
    public static readonly string[] SystemFileNames =
    {
        "pagefile.sys", "hiberfil.sys", "swapfile.sys", "bootmgr", "BOOTNXT", "BOOTSECT.BAK",
    };

    /// <summary>Top-level folders on any drive that belong to Windows.</summary>
    public static readonly string[] SystemTopFolders =
    {
        "System Volume Information", "$Recycle.Bin", "Recovery", "Boot", "EFI", "$WinREAgent", "$SysReset",
        "$Windows.~BT", "$Windows.~WS", "$WINDOWS.~Q", "$GetCurrent", "Config.Msi",
    };

    /// <summary>
    /// Why a single file may not be moved to the Recycle Bin by the analyser, or null if it may.
    /// <paramref name="norm"/> must already be normalised (null = couldn't be normalised → refuse).
    /// <paramref name="protectedRoots"/> are normalised folders (Windows, Program Files, …) whose contents are off-limits.
    /// </summary>
    public static string? WhyProtected(string? norm, IReadOnlyList<string> protectedRoots, FileAttributes? attrs = null)
    {
        if (norm == null) return "its location couldn't be checked";
        if (WinPath.IsRoot(norm)) return "it's the top of a drive";
        string name = WinPath.Name(norm);
        foreach (var s in SystemFileNames)
            if (string.Equals(name, s, StringComparison.OrdinalIgnoreCase)) return "Windows needs this file";
        string top = WinPath.FirstSegment(norm);
        foreach (var s in SystemTopFolders)
            if (string.Equals(top, s, StringComparison.OrdinalIgnoreCase)) return "it's part of Windows";
        foreach (var r in protectedRoots)
            if (WinPath.IsSameOrInside(norm, r)) return "it's inside a Windows or program folder (" + r + ")";
        if (attrs is FileAttributes a)
        {
            if ((a & FileAttributes.ReparsePoint) != 0) return "it's a link to somewhere else";
            if ((a & FileAttributes.Directory) != 0) return "it's a folder";
            if ((a & FileAttributes.System) != 0) return "it's marked as a system file";
        }
        return null;
    }

    /// <summary>
    /// Why a cleanup root is unsafe, or null if it's fine. A root must be a local drive-letter folder, not a
    /// drive root, not one of the <paramref name="criticalFolders"/> (Windows, Program Files, your profile, …)
    /// unless it's non-recursive with an exact file name (e.g. %WINDIR% + "MEMORY.DMP"), and – for recursive
    /// roots – it must not contain any critical folder.
    /// </summary>
    public static string? WhyBadCleanupRoot(string? norm, bool recursive, string? pattern, IReadOnlyList<string> criticalFolders)
    {
        if (norm == null) return "the folder couldn't be resolved";
        if (WinPath.IsUnc(norm)) return "it's on a network share";
        if (WinPath.IsRoot(norm)) return "it's the top of a drive";
        bool exactName = pattern != null && pattern.IndexOfAny(new[] { '*', '?' }) < 0;
        foreach (var c in criticalFolders)
        {
            if (WinPath.SamePath(norm, c) && (recursive || !exactName)) return "it's an important folder (" + c + ")";
            if (recursive && WinPath.IsInside(c, norm)) return "it contains important folders (" + c + ")";
        }
        return null;
    }

    /// <summary>
    /// The raw TEMP/TMP value must be a fully-qualified Windows path with no unexpanded '%' variables.
    /// (Production code also calls Path.IsPathFullyQualified.)
    /// </summary>
    public static string? WhyBadTempValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "it isn't set";
        if (value.Contains('%')) return "it contains an unexpanded %variable%";
        if (WinPath.Normalize(value) == null) return "it isn't a full path";
        return null;
    }

    /// <summary>
    /// Only %LOCALAPPDATA%\Temp itself, or a numbered per-session folder directly inside it ("…\Temp\2"), is
    /// accepted as the user's temp folder. Anything else (TEMP pointed at C:\, Documents, D:\Scratch…) isn't offered.
    /// </summary>
    public static string? WhyBadTempRoot(string? norm, string? localAppDataTempNorm)
    {
        if (norm == null || localAppDataTempNorm == null) return "the folder couldn't be resolved";
        if (WinPath.SamePath(norm, localAppDataTempNorm)) return null;
        if (WinPath.SamePath(WinPath.Parent(norm), localAppDataTempNorm) && IsAllDigits(WinPath.Name(norm))) return null;
        return "it isn't your normal Temp folder (" + localAppDataTempNorm + ")";
    }

    /// <summary>
    /// A cleanup root must be strictly inside its expected parent (e.g. %LOCALAPPDATA%), both as given and where it
    /// really lives. Null = fine.
    /// </summary>
    public static string? WhyNotUnderParent(string? rootNorm, string? parentNorm) =>
        rootNorm != null && parentNorm != null && WinPath.IsInside(rootNorm, parentNorm) ? null : "its location looks redirected";

    /// <summary>
    /// Where a cleanup root sits, judged on REAL locations (final paths from handles – always long names, so 8.3
    /// short names in %TEMP% can't cause false refusals) plus the given path after short-name expansion:
    /// • a temp root's real location must be the real %LOCALAPPDATA%\Temp (or Temp\N) → otherwise refused;
    /// • its real location must be strictly inside the parent's real location, and the given path must be the
    ///   parent plus the same relative path (so no link sits between them) → otherwise "redirected".
    /// Returns (redirected, why); why == null means fine.
    /// </summary>
    public static (bool redirected, string? why) CheckPlacement(string? rootNorm, string? parentNorm, string? rootFinal,
        string? parentFinal, bool tempRoot, string? localTempFinal)
    {
        if (rootFinal == null || parentFinal == null) return (true, "its location couldn't be checked");
        if (tempRoot && WhyBadTempRoot(rootFinal, localTempFinal) is string t) return (false, t);
        if (WhyNotUnderParent(rootFinal, parentFinal) is string w) return (true, w);
        if (WhyNotUnderParent(rootNorm, parentNorm) is string w2) return (true, w2);
        string relGiven = Relative(rootNorm!, parentNorm!), relReal = Relative(rootFinal, parentFinal);
        return WinPath.SamePath(relGiven, relReal) ? (false, null) : (true, "its location looks redirected");
    }

    private static string Relative(string child, string parent) =>
        child[(parent.EndsWith('\\') ? parent.Length : parent.Length + 1)..];

    /// <summary>True if the path is the same as or inside any of the folders (normalised).</summary>
    public static bool IsUnderAny(string? norm, IEnumerable<string> folders) =>
        norm != null && folders.Any(f => WinPath.IsSameOrInside(norm, f));

    private static bool IsAllDigits(string s)
    {
        if (s.Length == 0 || s.Length > 4) return false;
        foreach (char c in s) if (c < '0' || c > '9') return false;
        return true;
    }
}
