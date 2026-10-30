using System.IO.Enumeration;

namespace WinScope.Modules.Storage;

/// <summary>One directory entry as enumerated (no extra disk access).</summary>
internal struct DirEntry
{
    /// <summary>Null for small files when the caller asked to skip names below a size (saves allocations).</summary>
    public string? Name;
    public FileAttributes Attributes;
    public long Length;
    public DateTime CreationUtc, WriteUtc;
    public bool IsDirectory;
}

internal enum ListStatus { Ok, Missing, Denied, Failed, Cancelled }

/// <summary>
/// Single-directory listing (never recursive – callers walk with an explicit stack and decide themselves
/// whether to go into a sub-folder, so reparse points are never followed).
/// </summary>
internal static class FsWalk
{
    private static readonly EnumerationOptions Options = new()
    {
        IgnoreInaccessible = true,       // a folder we can't read lists as empty instead of throwing
        AttributesToSkip = 0,            // include hidden and system entries; we check attributes ourselves
        RecurseSubdirectories = false,
        ReturnSpecialDirectories = false,
    };

    /// <summary>
    /// Fill <paramref name="into"/> with the entries of <paramref name="dir"/>. Names of files smaller than
    /// <paramref name="nameMinLength"/> are left null (folders always get a name).
    /// </summary>
    public static ListStatus List(string dir, List<DirEntry> into, long nameMinLength = 0, CancellationToken ct = default)
    {
        into.Clear();
        try
        {
            var e = new FileSystemEnumerable<DirEntry>(dir, (ref FileSystemEntry x) => new DirEntry
            {
                Name = x.IsDirectory || x.Length >= nameMinLength ? x.FileName.ToString() : null,
                Attributes = x.Attributes,
                Length = x.IsDirectory ? 0 : x.Length,
                CreationUtc = x.CreationTimeUtc.UtcDateTime,
                WriteUtc = x.LastWriteTimeUtc.UtcDateTime,
                IsDirectory = x.IsDirectory,
            }, Options);
            foreach (var d in e)
            {
                if (ct.IsCancellationRequested) return ListStatus.Cancelled;     // checked per entry: huge folders stop promptly
                into.Add(d);
            }
            return ListStatus.Ok;
        }
        catch (DirectoryNotFoundException) { return ListStatus.Missing; }
        catch (UnauthorizedAccessException) { return ListStatus.Denied; }
        catch (IOException) { return ListStatus.Failed; }
        catch (ArgumentException) { return ListStatus.Failed; }
        catch (NotSupportedException) { return ListStatus.Failed; }
    }

    /// <summary>dir + "\" + name, without doubling the separator after a drive root. (On Windows the separator is
    /// always '\'; using the platform's lets the scanner be unit-tested elsewhere.)</summary>
    public static string Join(string dir, string name) =>
        dir.Length > 0 && (dir[^1] == '\\' || dir[^1] == Path.DirectorySeparatorChar) ? dir + name : dir + Path.DirectorySeparatorChar + name;
}
