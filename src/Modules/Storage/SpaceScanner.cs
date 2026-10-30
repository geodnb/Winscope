namespace WinScope.Modules.Storage;

/// <summary>
/// One folder in a space scan. Only folders are stored (never individual files) so a whole-drive scan stays
/// small: roughly 150–250 bytes per folder.
/// </summary>
internal sealed class DirNode
{
    /// <summary>Folder name; for the scan root, its full path.</summary>
    public string Name;
    public DirNode? Parent;
    /// <summary>Total bytes in this folder and everything below it (filled in when the scan finishes).</summary>
    public long Size;
    /// <summary>Total files in this folder and below.</summary>
    public long Files;
    /// <summary>Bytes / file count directly inside this folder.</summary>
    public long OwnSize;
    public int OwnFiles;
    public List<DirNode>? Children;
    /// <summary>A junction/symlink/mount point: recorded with zero size and never entered.</summary>
    public bool IsLink;

    public DirNode(string name, DirNode? parent) { Name = name; Parent = parent; }

    public string FullPath
    {
        get
        {
            if (Parent == null) return Name;
            var parts = new List<string>();
            for (var n = this; n.Parent != null; n = n.Parent) parts.Add(n.Name);
            var root = this;
            while (root.Parent != null) root = root.Parent;
            string p = root.Name;
            for (int i = parts.Count - 1; i >= 0; i--) p = FsWalk.Join(p, parts[i]);
            return p;
        }
    }
}

internal sealed record BigFile(string Path, long Size, DateTime WriteUtc)
{
    public string Name => System.IO.Path.GetFileName(Path);
    public string Folder => System.IO.Path.GetDirectoryName(Path) ?? "";
}

/// <summary>Live counters for the progress line (written by the scan thread, read by a UI timer).</summary>
internal sealed class ScanProgress
{
    public long Dirs, Files, Bytes;
    public volatile string Current = "";
}

internal sealed class ScanResult
{
    public required DirNode Root { get; init; }
    public required string RootPath { get; init; }
    public required List<BigFile> Largest { get; init; }
    public long Dirs, Files, Bytes, CloudFiles, CloudBytes, Links;
    /// <summary>Folders that listed as empty because Windows wouldn't let us read them.</summary>
    public long Unreadable;
    public bool Cancelled;
    public TimeSpan Took;
    /// <summary>For a whole-drive scan: the drive's used space, so the gap (system files, unreadable folders) can be shown.</summary>
    public long? DriveUsed { get; set; }
}

internal static class SpaceScanner
{
    public const int KeepLargest = 200;

    /// <summary>
    /// Walk <paramref name="rootPath"/> with an explicit stack (no recursion, so deep trees can't overflow).
    /// Reparse-point folders are recorded as zero-size links and never entered; online-only cloud files are
    /// counted separately and not added to sizes. Stops promptly when cancelled and returns what it has.
    /// </summary>
    public static ScanResult Scan(string rootPath, ScanProgress progress, CancellationToken ct, Func<string, bool>? isUnreadable = null)
    {
        var started = DateTime.UtcNow;
        var root = new DirNode(rootPath, null);
        var order = new List<DirNode>(1024);
        var stack = new Stack<(DirNode node, string path)>();
        var top = new TopN<BigFile>(KeepLargest);
        var buf = new List<DirEntry>(256);
        long dirs = 0, files = 0, bytes = 0, cloudFiles = 0, cloudBytes = 0, links = 0, unreadable = 0;
        bool cancelled = false;
        stack.Push((root, rootPath));

        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }
            var (node, path) = stack.Pop();
            order.Add(node);
            progress.Current = path;

            // Only allocate names for files big enough to enter the top list (folders always get names).
            long nameMin = top.IsFull ? top.Threshold + 1 : 0;
            var st = FsWalk.List(path, buf, nameMin, ct);
            if (st == ListStatus.Cancelled) { cancelled = true; break; }
            // Inaccessible folders list as empty (IgnoreInaccessible), so ask whether an empty one was really unreadable.
            if (st is ListStatus.Denied or ListStatus.Failed || (buf.Count == 0 && isUnreadable != null && SafeCheck(isUnreadable, path)))
                unreadable++;

            foreach (var e in buf)
            {
                if (e.IsDirectory)
                {
                    var child = new DirNode(e.Name ?? "?", node);
                    (node.Children ??= new List<DirNode>()).Add(child);
                    dirs++;
                    if (FileRules.IsLink(e.Attributes)) { child.IsLink = true; links++; }
                    else if (e.Name != null) stack.Push((child, FsWalk.Join(path, e.Name)));
                    continue;
                }
                if (FileRules.IsLink(e.Attributes)) { links++; continue; }          // file symlink: no size of its own
                if (FileRules.IsCloudOnly(e.Attributes)) { cloudFiles++; cloudBytes += e.Length; continue; }
                files++;
                node.OwnFiles++;
                node.OwnSize += e.Length;
                bytes += e.Length;
                if (e.Name != null && (!top.IsFull || e.Length > top.Threshold))
                    top.Add(new BigFile(FsWalk.Join(path, e.Name), e.Length, e.WriteUtc), e.Length);
            }
            node.Children?.TrimExcess();
            Interlocked.Exchange(ref progress.Dirs, dirs);
            Interlocked.Exchange(ref progress.Files, files);
            Interlocked.Exchange(ref progress.Bytes, bytes);
        }

        // Children always come after their parent in 'order', so a reverse pass totals bottom-up.
        for (int i = order.Count - 1; i >= 0; i--)
        {
            var n = order[i];
            n.Size += n.OwnSize;
            n.Files += n.OwnFiles;
            if (n.Parent != null) { n.Parent.Size += n.Size; n.Parent.Files += n.Files; }
        }

        return new ScanResult
        {
            Root = root, RootPath = rootPath, Largest = top.ToSortedList(),
            Dirs = dirs, Files = files, Bytes = bytes, CloudFiles = cloudFiles, CloudBytes = cloudBytes, Links = links, Unreadable = unreadable,
            Cancelled = cancelled, Took = DateTime.UtcNow - started,
        };
    }

    private static bool SafeCheck(Func<string, bool> f, string path)
    {
        try { return f(path); } catch { return false; }
    }

    /// <summary>Subtract a removed file from its folder and every folder above it in the tree.</summary>
    public static void Forget(ScanResult r, BigFile f)
    {
        string? rootNorm = WinPath.Normalize(r.RootPath);
        string? fileNorm = WinPath.Normalize(f.Path);
        if (rootNorm == null || fileNorm == null || !WinPath.IsInside(fileNorm, rootNorm)) return;
        string rel = fileNorm[(rootNorm.EndsWith('\\') ? rootNorm.Length : rootNorm.Length + 1)..];
        var segs = rel.Split('\\');
        var n = r.Root;
        for (int i = 0; i < segs.Length - 1 && n != null; i++)
            n = n.Children?.FirstOrDefault(c => !c.IsLink && string.Equals(c.Name, segs[i], StringComparison.OrdinalIgnoreCase));
        if (n == null) return;
        n.OwnSize = Math.Max(0, n.OwnSize - f.Size);
        n.OwnFiles = Math.Max(0, n.OwnFiles - 1);
        for (var p = n; p != null; p = p.Parent) { p.Size = Math.Max(0, p.Size - f.Size); p.Files = Math.Max(0, p.Files - 1); }
        r.Bytes = Math.Max(0, r.Bytes - f.Size);
        r.Files = Math.Max(0, r.Files - 1);
        r.Largest.Remove(f);
    }
}
