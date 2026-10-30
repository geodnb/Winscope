using System.Globalization;
using System.IO.Enumeration;

namespace WinScope.Modules.Storage;

// Pure logic (no WPF, no P/Invoke) – unit-tested in a scratch console project.

/// <summary>Per-file filters shared by the cleanup preview and the delete-time re-check.</summary>
public static class FileRules
{
    // winnt.h FILE_ATTRIBUTE_* values not named in System.IO.FileAttributes.
    public const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;        // FILE_ATTRIBUTE_RECALL_ON_OPEN
    public const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;  // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
    public const FileAttributes CloudMask = RecallOnOpen | RecallOnDataAccess | FileAttributes.Offline; // 0x1000 OFFLINE

    /// <summary>OneDrive/iCloud "online-only" placeholder: the data isn't on this disk.</summary>
    public static bool IsCloudOnly(FileAttributes a) => (a & CloudMask) != 0;

    /// <summary>Junction, symlink, mount point or any other reparse point.</summary>
    public static bool IsLink(FileAttributes a) => (a & FileAttributes.ReparsePoint) != 0;

    /// <summary>Simple '*'/'?' file-name match, case-insensitive; null pattern matches everything.</summary>
    public static bool MatchesPattern(string name, string? pattern) =>
        pattern == null || (name.Length > 0 && FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true));

    /// <summary>
    /// Whether a file is old enough for an age-limited category. Uses the NEWER of the creation and
    /// last-write times: installers often extract files into Temp keeping an old "modified" date, and those
    /// must count as new. Future timestamps count as new. No limit → always true.
    /// </summary>
    public static bool IsOldEnough(DateTime creationUtc, DateTime writeUtc, DateTime nowUtc, TimeSpan? minAge) =>
        IsOldEnough(creationUtc, writeUtc, DateTime.MinValue, nowUtc, minAge);

    /// <summary>As above, also counting the NTFS change time (moved/renamed/attributes changed) – so a file or folder
    /// that was just moved in, keeping its old dates, still gets the grace period.</summary>
    public static bool IsOldEnough(DateTime creationUtc, DateTime writeUtc, DateTime changeUtc, DateTime nowUtc, TimeSpan? minAge)
    {
        if (minAge is not TimeSpan age) return true;
        var newest = creationUtc > writeUtc ? creationUtc : writeUtc;
        if (changeUtc > newest) newest = changeUtc;
        return nowUtc - newest >= age;
    }

    /// <summary>
    /// Full category filter for one file as seen at enumeration or via an open handle:
    /// not a link, not a folder, not cloud-only, name matches, old enough.
    /// </summary>
    public static bool Qualifies(string name, FileAttributes attrs, DateTime creationUtc, DateTime writeUtc,
                                 DateTime nowUtc, string? pattern, TimeSpan? minAge, DateTime? changeUtc = null) =>
        !IsLink(attrs) && (attrs & FileAttributes.Directory) == 0 && !IsCloudOnly(attrs) && !HasOddEnd(name) &&
        MatchesPattern(name, pattern) && IsOldEnough(creationUtc, writeUtc, changeUtc ?? DateTime.MinValue, nowUtc, minAge);

    /// <summary>A name ending in '.' or ' ' – Win32 path APIs would act on a different (trimmed) name.</summary>
    public static bool HasOddEnd(string name) => name.Length > 0 && (name[^1] == '.' || name[^1] == ' ') && name != "." && name != "..";

    /// <summary>Chromium profile folder names we clean caches in: "Default" or "Profile N".</summary>
    public static bool IsChromiumProfileName(string name)
    {
        if (string.Equals(name, "Default", StringComparison.Ordinal)) return true;
        if (!name.StartsWith("Profile ", StringComparison.Ordinal)) return false;
        string n = name[8..];
        if (n.Length is 0 or > 4) return false;
        foreach (char c in n) if (c < '0' || c > '9') return false;
        return true;
    }
}

/// <summary>What an open handle says about a file or folder (read via GetFileInformationByHandleEx).</summary>
public readonly record struct FileFacts(
    FileAttributes Attributes, DateTime CreationUtc, DateTime WriteUtc, long Size, uint Links, bool IsDirectory,
    DateTime ChangeUtc = default, bool DeletePending = false);

/// <summary>
/// The delete-time decision for one cleanup file, split out so it can be tested with synthetic data.
/// Stage 1 runs on the path as built from the enumeration; stage 2 on what the opened handle reports.
/// </summary>
public static class DeleteGate
{
    /// <summary>Stage 1: the path as built must be strictly inside the root as given (and directly in it when not recursive).</summary>
    public static bool PathAllowed(string rawPath, string rootNorm, bool recurse)
    {
        string? n = WinPath.Normalize(rawPath);
        if (n == null || !WinPath.IsInside(n, rootNorm)) return false;
        return recurse || WinPath.SamePath(WinPath.Parent(n), rootNorm);
    }

    /// <summary>
    /// Stage 2: null = delete it; otherwise why not. <paramref name="finalRaw"/> is where the handle really points,
    /// exactly as GetFinalPathNameByHandleW returned it; <paramref name="rootFinal"/> is where the category root
    /// really is (normalised).
    /// </summary>
    public static SkipReason? Check(FileFacts f, string? finalRaw, string rootFinal, bool recurse, string? pattern,
                                    TimeSpan? minAge, DateTime nowUtc)
    {
        if (f.IsDirectory || FileRules.IsLink(f.Attributes)) return SkipReason.Changed;
        if (f.Links > 1) return SkipReason.Linked;            // another name elsewhere: deleting this one frees nothing
        if (f.DeletePending) return SkipReason.Changed;        // someone else is already deleting it
        if (finalRaw == null) return SkipReason.Other;
        if (WinPath.HasTrailingDotOrSpaceSegment(finalRaw)) return SkipReason.Unusual;
        string? finalNorm = WinPath.Normalize(finalRaw);
        if (finalNorm == null) return SkipReason.Unusual;
        if (!FileRules.Qualifies(WinPath.Name(finalNorm), f.Attributes, f.CreationUtc, f.WriteUtc, nowUtc, pattern, minAge, f.ChangeUtc))
            return SkipReason.Changed;
        if (!WinPath.IsInside(finalNorm, rootFinal)) return SkipReason.Changed;
        if (!recurse && !WinPath.SamePath(WinPath.Parent(finalNorm), rootFinal)) return SkipReason.Changed;
        return null;
    }

    /// <summary>Whether an (expected-empty) sub-folder may be removed: a real folder, strictly inside the root, old enough.</summary>
    public static bool DirRemovable(FileFacts f, string? finalRaw, string rootFinal, TimeSpan? minAge, DateTime nowUtc)
    {
        if (!f.IsDirectory || FileRules.IsLink(f.Attributes)) return false;
        if (minAge is TimeSpan age && nowUtc - f.CreationUtc < age) return false;
        if (finalRaw == null || WinPath.HasTrailingDotOrSpaceSegment(finalRaw)) return false;
        return WinPath.IsInside(WinPath.Normalize(finalRaw), rootFinal);
    }

    /// <summary>
    /// Whether a folder is safe to enter (<c>safe</c>): a real folder (not a link), with a normal name, really
    /// strictly inside the root. <c>old</c>: for age-limited categories, neither it nor any folder between it and
    /// the root (<paramref name="parentsOld"/> carries that down) was created, changed or had something moved in
    /// during the grace period – a recent folder isn't entered at all, so everything below it gets the grace too.
    /// </summary>
    public static (bool safe, bool old) DirEnterable(FileFacts f, string? finalRaw, string rootFinal, TimeSpan? minAge,
                                                     DateTime nowUtc, bool parentsOld)
    {
        if (!f.IsDirectory || FileRules.IsLink(f.Attributes)) return (false, false);
        if (finalRaw == null || WinPath.HasTrailingDotOrSpaceSegment(finalRaw)) return (false, false);
        if (!WinPath.IsInside(WinPath.Normalize(finalRaw), rootFinal)) return (false, false);
        return (true, parentsOld && FileRules.IsOldEnough(f.CreationUtc, f.WriteUtc, f.ChangeUtc, nowUtc, minAge));
    }
}

/// <summary>Stops a category that turns out much bigger than its preview (something changed – re-check first).</summary>
public static class GrowthLimit
{
    public const double Factor = 1.25;
    public const long FileSlack = 5;
    public const long ByteSlack = 16L * 1024 * 1024;

    public static long MaxFiles(long preview) => Math.Max((long)Math.Ceiling(preview * Factor), preview + FileSlack);
    public static long MaxBytes(long preview) => Math.Max((long)Math.Ceiling(preview * Factor), preview + ByteSlack);

    /// <summary>True if deleting one more file of <paramref name="nextSize"/> would go past the limit.</summary>
    public static bool WouldExceed(long previewFiles, long previewBytes, long doneFiles, long doneBytes, long nextSize) =>
        doneFiles + 1 > MaxFiles(previewFiles) || doneBytes + nextSize > MaxBytes(previewBytes);
}

/// <summary>Keeps the N items with the largest keys (a bounded min-heap). Not thread-safe.</summary>
public sealed class TopN<T>
{
    private readonly PriorityQueue<T, long> _q = new();
    public int Capacity { get; }
    public TopN(int capacity) { Capacity = Math.Max(1, capacity); }

    public int Count => _q.Count;
    public bool IsFull => _q.Count >= Capacity;

    /// <summary>Smallest key kept so far once full (an item must beat it to get in); long.MinValue until full.</summary>
    public long Threshold => IsFull && _q.TryPeek(out _, out long k) ? k : long.MinValue;

    public void Add(T item, long key)
    {
        if (_q.Count < Capacity) { _q.Enqueue(item, key); return; }
        if (_q.TryPeek(out _, out long min) && key > min) _q.DequeueEnqueue(item, key);
    }

    /// <summary>Largest first.</summary>
    public List<T> ToSortedList()
    {
        var all = new List<(T item, long key)>(_q.UnorderedItems.Select(x => (x.Element, x.Priority)));
        all.Sort((a, b) => b.key.CompareTo(a.key));
        return all.Select(x => x.item).ToList();
    }
}

/// <summary>Would the Recycle Bin on that drive really take this file, or would Windows delete it for good?</summary>
public static class RecycleRules
{
    /// <summary>Fallback when the bin's size isn't known: Windows' default bin is about 5% of the drive.</summary>
    public const double FallbackShare = 0.05;

    /// <summary>
    /// Null if the file fits; otherwise why not. <paramref name="maxBytes"/> is the bin's configured maximum for that
    /// drive (null = unknown), <paramref name="nukeOnDelete"/> whether the bin is turned off for it.
    /// </summary>
    public static string? WhyTooBig(long fileSize, long? maxBytes, bool nukeOnDelete, long driveTotal)
    {
        if (nukeOnDelete) return "the Recycle Bin is turned off for this drive, so Windows would delete it for good";
        if (maxBytes is long max)
            return fileSize > max ? "it's bigger than this drive's Recycle Bin can hold, so Windows would delete it for good" : null;
        if (driveTotal <= 0) return "WinScope couldn't tell how big this drive's Recycle Bin is";
        return fileSize > driveTotal * FallbackShare
            ? "it may be too big for this drive's Recycle Bin (over 5% of the drive), so Windows could delete it for good"
            : null;
    }
}

public enum SkipReason { InUse, Denied, Changed, Linked, Other, Unusual }

/// <summary>Why files were left alone during a clean.</summary>
public sealed class SkipCounts
{
    private readonly long[] _n = new long[Enum.GetValues<SkipReason>().Length];
    public void Add(SkipReason r, long count = 1) => _n[(int)r] += count;
    public long this[SkipReason r] => _n[(int)r];
    public long Total => _n.Sum();
    public void AddAll(SkipCounts o) { for (int i = 0; i < _n.Length; i++) _n[i] += o._n[i]; }

    /// <summary>"3 in use, 1 access denied" – empty when nothing was skipped.</summary>
    public string Describe()
    {
        var parts = new List<string>();
        if (this[SkipReason.InUse] > 0) parts.Add($"{N(this[SkipReason.InUse])} in use");
        if (this[SkipReason.Denied] > 0) parts.Add($"{N(this[SkipReason.Denied])} access denied");
        if (this[SkipReason.Changed] > 0) parts.Add($"{N(this[SkipReason.Changed])} changed or moved since the check");
        if (this[SkipReason.Linked] > 0) parts.Add($"{N(this[SkipReason.Linked])} links or hard-linked files");
        if (this[SkipReason.Unusual] > 0) parts.Add($"{N(this[SkipReason.Unusual])} with unusual names");
        if (this[SkipReason.Other] > 0) parts.Add($"{N(this[SkipReason.Other])} other errors");
        return string.Join(", ", parts);
    }

    private static string N(long v) => v.ToString("N0", CultureInfo.InvariantCulture);
}

public static class CleanupLog
{
    /// <summary>One line of %AppData%\WinScope\cleanup-log.txt (tab separated so it opens cleanly in a spreadsheet).</summary>
    public static string FormatLine(DateTime local, string category, long files, long bytes, SkipCounts skipped, string? note = null)
    {
        var c = CultureInfo.InvariantCulture;
        string skip = skipped.Total == 0 ? "skipped 0" : $"skipped {skipped.Total.ToString(c)} ({skipped.Describe()})";
        string line = $"{local.ToString("yyyy-MM-dd HH:mm:ss", c)}\t{Clean(category)}\tfiles {files.ToString(c)}\t" +
                      $"bytes {bytes.ToString(c)} ({(bytes / 1048576.0).ToString("0.0", c)} MB)\t{skip}";
        if (!string.IsNullOrEmpty(note)) line += "\t" + Clean(note);
        return line;
    }

    private static string Clean(string s) => s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
