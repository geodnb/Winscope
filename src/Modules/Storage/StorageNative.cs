using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScope.Modules.Storage;

/// <summary>Volume serial + 128-bit file id (FILE_ID_INFO): identifies a file or folder regardless of its name.</summary>
internal readonly record struct FileId(ulong Volume, ulong Low, ulong High);

/// <summary>
/// P/Invoke for Storage. Struct layouts are written/read by explicit offsets (x64 – the csproj targets x64).
/// DLLs load from System32 only (assembly-wide DefaultDllImportSearchPaths in GlobalUsings.cs).
/// </summary>
internal static class StorageNative
{
    // winnt.h access rights / fileapi.h flags
    private const uint DELETE = 0x00010000;
    private const uint FILE_READ_ATTRIBUTES = 0x0080;
    private const uint FILE_LIST_DIRECTORY = 0x0001;
    private const uint FILE_SHARE_READ_WRITE_DELETE = 0x1 | 0x2 | 0x4;
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;   // open the link itself, never its target
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;     // required to open directories

    // FILE_INFO_BY_HANDLE_CLASS (minwinbase.h)
    private const int FileBasicInfo = 0;
    private const int FileStandardInfo = 1;
    private const int FileDispositionInfo = 4;
    private const int FileIdInfo = 18;

    // GetFinalPathNameByHandleW flags
    private const uint FILE_NAME_NORMALIZED = 0x0, VOLUME_NAME_DOS = 0x0;

    // winerror.h
    public const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ACCESS_DENIED = 5,
        ERROR_SHARING_VIOLATION = 32, ERROR_LOCK_VIOLATION = 33, ERROR_DIR_NOT_EMPTY = 145, ERROR_CANCELLED = 1223;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle hFile, int fileInformationClass, IntPtr lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(SafeFileHandle hFile, int fileInformationClass, IntPtr lpFileInformation, uint dwBufferSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetFinalPathNameByHandleW(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(string lpszVolumeMountPoint, [Out] char[] lpszVolumeName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetLongPathNameW(string lpszShortPath, [Out] char[] lpszLongPath, uint cchBuffer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(string lpszFileName, [Out] char[] lpszVolumePathName, uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint GetDriveTypeW(string lpRootPathName);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string lpDirectoryName, out ulong lpFreeBytesAvailableToCaller,
        out ulong lpTotalNumberOfBytes, out ulong lpTotalNumberOfFreeBytes);

    private const uint DRIVE_FIXED = 3;

    /// <summary>
    /// Expand 8.3 short names ("C:\Users\SAMTAY~1\…" → "C:\Users\Sam Taylor\…") with GetLongPathNameW.
    /// Path.GetFullPath doesn't do this. Returns the input unchanged if it has no '~' or can't be expanded
    /// (e.g. it doesn't exist) – callers then compare real final paths, which are always long.
    /// </summary>
    public static string ExpandShortNames(string fullPath)
    {
        if (fullPath.IndexOf('~') < 0) return fullPath;
        try
        {
            string ext = Extended(fullPath);
            uint cap = 1024;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var buf = new char[cap];
                uint n = GetLongPathNameW(ext, buf, cap);
                if (n == 0) return fullPath;
                if (n < cap) return WinPath.StripExtendedPrefix(new string(buf, 0, (int)n));
                if (n > WinPath.MaxLength + 16) return fullPath;
                cap = n + 1;
            }
        }
        catch { }
        return fullPath;
    }

    /// <summary>The volume mount point a path lives on ("C:\" or, for a drive mounted in a folder, "C:\Mount\Disk2\").</summary>
    public static string? VolumeMountPoint(string path)
    {
        var buf = new char[WinPath.MaxLength + 1];
        if (!GetVolumePathNameW(path, buf, (uint)buf.Length)) return null;
        int n = Array.IndexOf(buf, '\0');
        if (n <= 0) return null;
        string m = new string(buf, 0, n);
        return m.EndsWith('\\') ? m : m + "\\";
    }

    /// <summary>Whether the volume at this mount point is a fixed (internal) disk.</summary>
    public static bool IsFixedVolume(string mountPoint) => GetDriveTypeW(mountPoint) == DRIVE_FIXED;

    /// <summary>Total size of the volume at this mount point, or 0.</summary>
    public static long VolumeTotalBytes(string mountPoint) =>
        GetDiskFreeSpaceExW(mountPoint, out _, out ulong total, out _) ? (long)Math.Min(total, long.MaxValue) : 0;

    // ------------------------------------------------------------------ handles

    /// <summary>
    /// "\\?\" form for CreateFileW so paths over MAX_PATH work without the app being long-path aware, and so the
    /// exact name is opened (no trimming of trailing dots/spaces). Input must be an absolute, already-clean path.
    /// </summary>
    private static string Extended(string full)
    {
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + full[2..];
        return @"\\?\" + full;
    }

    /// <summary>
    /// Open a file or folder WITHOUT following reparse points. <paramref name="forDelete"/> asks for DELETE
    /// access (needed to mark it for deletion); otherwise only attributes are read. Sharing allows everyone else
    /// to keep reading/writing/deleting, so an in-use file only fails if its owner denied sharing (→ "in use").
    /// </summary>
    public static SafeFileHandle Open(string fullPath, bool forDelete, bool listDirectory, out int error)
    {
        uint access = FILE_READ_ATTRIBUTES | (forDelete ? DELETE : 0) | (listDirectory ? FILE_LIST_DIRECTORY : 0);
        var h = CreateFileW(Extended(fullPath), access, FILE_SHARE_READ_WRITE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_OPEN_REPARSE_POINT | FILE_FLAG_BACKUP_SEMANTICS, IntPtr.Zero);
        error = h.IsInvalid ? Marshal.GetLastWin32Error() : 0;
        return h;
    }

    /// <summary>FILE_BASIC_INFO + FILE_STANDARD_INFO for an open handle.</summary>
    public static bool TryGetFacts(SafeFileHandle h, out FileFacts facts)
    {
        facts = default;
        IntPtr buf = Marshal.AllocHGlobal(64);
        try
        {
            Zero(buf, 64);
            // FILE_BASIC_INFO (40 bytes): LARGE_INTEGER CreationTime @0, LastAccessTime @8, LastWriteTime @16,
            // ChangeTime @24, DWORD FileAttributes @32, 4 bytes padding.
            if (!GetFileInformationByHandleEx(h, FileBasicInfo, buf, 40)) return false;
            long created = Marshal.ReadInt64(buf, 0);
            long written = Marshal.ReadInt64(buf, 16);
            long changed = Marshal.ReadInt64(buf, 24);
            uint attrs = (uint)Marshal.ReadInt32(buf, 32);

            Zero(buf, 64);
            // FILE_STANDARD_INFO (24 bytes): LARGE_INTEGER AllocationSize @0, EndOfFile @8, DWORD NumberOfLinks @16,
            // BOOLEAN DeletePending @20, BOOLEAN Directory @21, 2 bytes padding.
            if (!GetFileInformationByHandleEx(h, FileStandardInfo, buf, 24)) return false;
            long size = Marshal.ReadInt64(buf, 8);
            uint links = (uint)Marshal.ReadInt32(buf, 16);
            bool pending = Marshal.ReadByte(buf, 20) != 0;
            bool dir = Marshal.ReadByte(buf, 21) != 0;

            facts = new FileFacts((FileAttributes)attrs, FromFileTime(created), FromFileTime(written),
                Math.Max(0, size), links, dir || (attrs & (uint)FileAttributes.Directory) != 0, FromFileTime(changed), pending);
            return true;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>FILE_ID_INFO (24 bytes): ULONGLONG VolumeSerialNumber @0, FILE_ID_128 FileId @8 (16 bytes).</summary>
    public static FileId? TryGetFileId(SafeFileHandle h)
    {
        IntPtr buf = Marshal.AllocHGlobal(32);
        try
        {
            Zero(buf, 32);
            if (!GetFileInformationByHandleEx(h, FileIdInfo, buf, 24)) return null;
            var id = new FileId((ulong)Marshal.ReadInt64(buf, 0), (ulong)Marshal.ReadInt64(buf, 8), (ulong)Marshal.ReadInt64(buf, 16));
            return id.Low == 0 && id.High == 0 ? null : id;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Open a folder or file by path (no link following) and return its id, or null.</summary>
    public static FileId? FileIdOf(string fullPath)
    {
        using var h = Open(fullPath, forDelete: false, listDirectory: false, out _);
        return h.IsInvalid ? null : TryGetFileId(h);
    }

    /// <summary>
    /// The real location of an open handle exactly as Windows reports it (resolves junctions above it, 8.3 short
    /// names, mount points), without the "\\?\" prefix. Null if Windows can't say or it has no drive letter.
    /// </summary>
    public static string? FinalPathRaw(SafeFileHandle h)
    {
        uint cap = 1024;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buf = new char[cap];
            uint n = GetFinalPathNameByHandleW(h, buf, cap, FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
            if (n == 0) return null;
            if (n < cap) return WinPath.StripExtendedPrefix(new string(buf, 0, (int)n));
            if (n > WinPath.MaxLength + 16) return null;
            cap = n + 1;                                          // too small: n is the size needed incl. NUL
        }
        return null;
    }

    /// <summary><see cref="FinalPathRaw"/> normalised with <see cref="WinPath.Normalize"/> (for comparisons only).</summary>
    public static string? FinalPath(SafeFileHandle h) => FinalPathRaw(h) is string r ? WinPath.Normalize(r) : null;

    /// <summary>
    /// Mark the open file/folder for deletion (FILE_DISPOSITION_INFO.DeleteFile = TRUE); it goes when the last
    /// handle closes. Folders that aren't empty fail with ERROR_DIR_NOT_EMPTY, read-only files with ACCESS_DENIED.
    /// </summary>
    public static bool MarkForDelete(SafeFileHandle h, out int error)
    {
        IntPtr buf = Marshal.AllocHGlobal(8);
        try
        {
            Zero(buf, 8);
            Marshal.WriteByte(buf, 0, 1);                         // FILE_DISPOSITION_INFO { BOOLEAN DeleteFile @0 } size 1
            bool ok = SetFileInformationByHandle(h, FileDispositionInfo, buf, 1);
            error = ok ? 0 : Marshal.GetLastWin32Error();
            return ok;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>
    /// After our delete handle closed: is the name really gone? A file someone else still has open (with delete
    /// sharing) stays "delete pending" – opening it fails with ACCESS_DENIED – until they close it.
    /// </summary>
    public static bool IsReallyGone(string fullPath)
    {
        using var h = Open(fullPath, forDelete: false, listDirectory: false, out int err);
        return h.IsInvalid && IsGone(err);
    }

    /// <summary>True when Windows refuses to list this folder (it then enumerates as empty).</summary>
    public static bool IsUnreadable(string dir)
    {
        using var h = Open(dir, forDelete: false, listDirectory: true, out int err);
        return h.IsInvalid && err == ERROR_ACCESS_DENIED;
    }

    private static DateTime FromFileTime(long ft)
    {
        if (ft <= 0) return DateTime.MinValue;
        try { return DateTime.FromFileTimeUtc(ft); } catch (ArgumentOutOfRangeException) { return DateTime.MaxValue; }
    }

    private static void Zero(IntPtr p, int bytes)
    {
        for (int i = 0; i + 8 <= bytes; i += 8) Marshal.WriteInt64(p, i, 0);
    }

    public static SkipReason Classify(int win32Error) => win32Error switch
    {
        ERROR_SHARING_VIOLATION or ERROR_LOCK_VIOLATION => SkipReason.InUse,
        ERROR_ACCESS_DENIED => SkipReason.Denied,
        _ => SkipReason.Other,
    };

    public static bool IsGone(int win32Error) => win32Error is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND;

    /// <summary>"\\?\Volume{GUID}\" for a drive root like "C:\", or null.</summary>
    public static string? VolumeGuidPath(string driveRoot)
    {
        var buf = new char[64];
        if (!GetVolumeNameForVolumeMountPointW(driveRoot, buf, (uint)buf.Length)) return null;
        int n = Array.IndexOf(buf, '\0');
        return n <= 0 ? null : new string(buf, 0, n);
    }

    // ------------------------------------------------------------------ services (read-only)

    private const uint SC_MANAGER_CONNECT = 0x0001, SERVICE_QUERY_STATUS = 0x0004, SERVICE_STOPPED = 1;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenSCManagerW(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, IntPtr lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    /// <summary>True only if the service is definitely stopped; false if running/starting/stopping or unknown.</summary>
    public static bool IsServiceStopped(string name)
    {
        IntPtr scm = IntPtr.Zero, svc = IntPtr.Zero, buf = IntPtr.Zero;
        try
        {
            scm = OpenSCManagerW(null, null, SC_MANAGER_CONNECT);
            if (scm == IntPtr.Zero) return false;
            svc = OpenServiceW(scm, name, SERVICE_QUERY_STATUS);
            if (svc == IntPtr.Zero) return false;
            // SERVICE_STATUS (28 bytes): dwServiceType @0, dwCurrentState @4, dwControlsAccepted @8, dwWin32ExitCode @12,
            // dwServiceSpecificExitCode @16, dwCheckPoint @20, dwWaitHint @24.
            buf = Marshal.AllocHGlobal(32);
            Zero(buf, 32);
            if (!QueryServiceStatus(svc, buf)) return false;
            return (uint)Marshal.ReadInt32(buf, 4) == SERVICE_STOPPED;
        }
        catch { return false; }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
            if (svc != IntPtr.Zero) CloseServiceHandle(svc);
            if (scm != IntPtr.Zero) CloseServiceHandle(scm);
        }
    }

    // ------------------------------------------------------------------ shell (shellapi.h / shobjidl_core.h)

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;
    public const int E_UNEXPECTED = unchecked((int)0x8000FFFF);   // SHEmptyRecycleBin when it's already empty

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHQueryRecycleBinW(string? pszRootPath, IntPtr pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHEmptyRecycleBinW(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, out IntPtr ppv);

    /// <summary>Size and item count of the Recycle Bin on all drives, or null if Windows wouldn't say. Call on STA.</summary>
    public static (long bytes, long items)? QueryRecycleBin()
    {
        // SHQUERYRBINFO on x64, 24 bytes: DWORD cbSize @0 | (pad 4) | __int64 i64Size @8 | __int64 i64NumItems @16
        IntPtr buf = Marshal.AllocHGlobal(24);
        try
        {
            Zero(buf, 24);
            Marshal.WriteInt32(buf, 0, 24);
            int hr = SHQueryRecycleBinW(null, buf);
            if (hr != 0) return null;
            long size = Marshal.ReadInt64(buf, 8), items = Marshal.ReadInt64(buf, 16);
            return (Math.Max(0, size), Math.Max(0, items));
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    /// <summary>Query on an STA thread, giving up after <paramref name="timeout"/> or on cancellation (the query is abandoned).</summary>
    public static (long bytes, long items)? QueryRecycleBinBounded(TimeSpan timeout, CancellationToken ct)
    {
        var t = RunSta(QueryRecycleBin);
        try { return t.Wait(timeout, ct) ? t.Result : null; }
        catch (OperationCanceledException) { return null; }
        catch (AggregateException) { return null; }
    }

    /// <summary>Empty the Recycle Bin on all drives without any Windows UI. Returns the HRESULT. Call on STA.</summary>
    public static int EmptyRecycleBin(IntPtr hwnd) =>
        SHEmptyRecycleBinW(hwnd, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);

    private static readonly Guid FOLDERID_Downloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>The user's Downloads folder (wherever they've moved it), falling back to %USERPROFILE%\Downloads.</summary>
    public static string? DownloadsFolder()
    {
        IntPtr p = IntPtr.Zero;
        try
        {
            var id = FOLDERID_Downloads;
            if (SHGetKnownFolderPath(ref id, 0, IntPtr.Zero, out p) == 0 && p != IntPtr.Zero)
            {
                string? s = Marshal.PtrToStringUni(p);
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { }
        finally { if (p != IntPtr.Zero) Marshal.FreeCoTaskMem(p); }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return string.IsNullOrEmpty(profile) ? null : Path.Combine(profile, "Downloads");
    }

    /// <summary>Run a shell call on a fresh STA thread (shell APIs expect a single-threaded COM apartment).</summary>
    public static Task<T> RunSta<T>(Func<T> f)
    {
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var t = new Thread(() =>
        {
            try { tcs.TrySetResult(f()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }) { IsBackground = true, Name = "WinScope shell op" };
        t.SetApartmentState(ApartmentState.STA);
        t.Start();
        return tcs.Task;
    }

    // ------------------------------------------------------------------ IFileOperation (Recycle Bin moves)

    private static readonly Guid CLSID_FileOperation = new("3AD05575-8857-4850-9277-11B85BDB8E09");
    private static Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    // FILEOP_FLAGS / FOFX_* (shellapi.h, shobjidl_core.h)
    private const uint FOF_SILENT = 0x0004, FOF_NOCONFIRMATION = 0x0010, FOF_ALLOWUNDO = 0x0040, FOF_NOERRORUI = 0x0400,
        FOF_WANTNUKEWARNING = 0x4000, FOFX_RECYCLEONDELETE = 0x00080000;
    // TRANSFER_SOURCE_FLAGS (shobjidl_core.h): set on PreDeleteItem when the item is going to the Recycle Bin.
    private const uint TSF_DELETE_RECYCLE_IF_POSSIBLE = 0x80;
    private const uint SIGDN_FILESYSPATH = 0x80058000;
    private const int S_OK = 0;
    private static readonly int HR_CANCELLED = unchecked((int)0x800704C7);   // HRESULT_FROM_WIN32(ERROR_CANCELLED)

    public enum RecycleOutcome { Recycled, Vetoed, Failed, DeletedNotRecycled, Cancelled }

    /// <summary>
    /// Move ONE file to the Recycle Bin with IFileOperation. A progress sink vetoes the delete in PreDeleteItem
    /// unless Windows says it will recycle it (TSF_DELETE_RECYCLE_IF_POSSIBLE) and the item is exactly our path,
    /// and success requires PostDeleteItem to report a newly created Recycle Bin item. Call on an STA thread.
    /// </summary>
    public static (RecycleOutcome outcome, int hr) RecycleWithShell(string path, FileId expectedId, IntPtr hwnd)
    {
        IFileOperation? op = null;
        IntPtr item = IntPtr.Zero;
        var sink = new RecycleSink(path, expectedId);
        try
        {
            var type = Type.GetTypeFromCLSID(CLSID_FileOperation, throwOnError: true)!;
            op = (IFileOperation)Activator.CreateInstance(type)!;
            int hr = SHCreateItemFromParsingName(path, IntPtr.Zero, ref IID_IShellItem, out item);
            if (hr < 0 || item == IntPtr.Zero) return (RecycleOutcome.Failed, hr);
            hr = op.SetOperationFlags(FOF_ALLOWUNDO | FOFX_RECYCLEONDELETE | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING);
            if (hr < 0) return (RecycleOutcome.Failed, hr);
            if (hwnd != IntPtr.Zero) op.SetOwnerWindow(hwnd);
            hr = op.Advise(sink, out uint cookie);
            if (hr < 0) return (RecycleOutcome.Failed, hr);
            try
            {
                hr = op.DeleteItem(item, IntPtr.Zero);
                if (hr < 0) return (RecycleOutcome.Failed, hr);
                hr = op.PerformOperations();
                op.GetAnyOperationsAborted(out bool aborted);
                // A confirmed new Recycle Bin item means it moved, whatever PerformOperations returns afterwards.
                if (sink.Recycled) return (RecycleOutcome.Recycled, hr);
                if (sink.Vetoed) return (RecycleOutcome.Vetoed, hr);
                if (sink.DeletedWithoutRecycle) return (RecycleOutcome.DeletedNotRecycled, hr);
                if (aborted || hr == HR_CANCELLED) return (RecycleOutcome.Cancelled, hr);
                return (RecycleOutcome.Failed, hr);
            }
            finally { op.Unadvise(cookie); }
        }
        catch (COMException ex) { return (RecycleOutcome.Failed, ex.HResult); }
        finally
        {
            if (item != IntPtr.Zero) Marshal.Release(item);
            if (op != null) Marshal.FinalReleaseComObject(op);
        }
    }

    /// <summary>Our IFileOperationProgressSink: only lets the one expected item through, and only to the Recycle Bin.</summary>
    [ComVisible(true)]
    [ClassInterface(ClassInterfaceType.None)]
    private sealed class RecycleSink : IFileOperationProgressSink
    {
        private readonly string _expected;
        private readonly FileId _expectedId;
        private int _preDeletes;
        public bool Vetoed, Recycled, DeletedWithoutRecycle;

        public RecycleSink(string expected, FileId expectedId) { _expected = expected; _expectedId = expectedId; }

        private int Veto() { Vetoed = true; return HR_CANCELLED; }

        /// <summary>
        /// The item about to be deleted must still be the very file that was checked: same path, same FILE_ID_INFO,
        /// same real location, not a link (catches a same-name swap or a parent-junction swap after the check).
        /// </summary>
        private bool StillTheSameFile()
        {
            using var h = Open(_expected, forDelete: false, listDirectory: false, out _);
            if (h.IsInvalid || !TryGetFacts(h, out var f) || f.IsDirectory || FileRules.IsLink(f.Attributes)) return false;
            if (TryGetFileId(h) is not FileId id || id != _expectedId) return false;
            return string.Equals(FinalPathRaw(h), _expected, StringComparison.Ordinal);
        }

        public int PreDeleteItem(uint dwFlags, IntPtr psiItem)
        {
            try
            {
                if (++_preDeletes > 1) return Veto();                                       // only ever one file
                if ((dwFlags & TSF_DELETE_RECYCLE_IF_POSSIBLE) == 0) return Veto();         // would be permanent
                string? p = ItemPath(psiItem);
                if (p == null || !string.Equals(p, _expected, StringComparison.OrdinalIgnoreCase)) return Veto();
                if (!StillTheSameFile()) return Veto();
                return S_OK;
            }
            catch { return Veto(); }
        }

        public int PostDeleteItem(uint dwFlags, IntPtr psiItem, int hrDelete, IntPtr psiNewlyCreated)
        {
            // Only S_OK means it was really deleted; positive COPYENGINE_S_* codes mean skipped/not done.
            if (hrDelete == S_OK && psiNewlyCreated != IntPtr.Zero) Recycled = true;
            else if (hrDelete == S_OK) DeletedWithoutRecycle = true;
            return S_OK;
        }

        private static string? ItemPath(IntPtr psi)
        {
            if (psi == IntPtr.Zero) return null;
            object? o = null;
            try
            {
                o = Marshal.GetObjectForIUnknown(psi);
                if (o is not IShellItem si) return null;
                if (si.GetDisplayName(SIGDN_FILESYSPATH, out IntPtr name) < 0 || name == IntPtr.Zero) return null;
                try { return Marshal.PtrToStringUni(name); }
                finally { Marshal.FreeCoTaskMem(name); }
            }
            finally { if (o != null && Marshal.IsComObject(o)) Marshal.ReleaseComObject(o); }
        }

        public int StartOperations() => S_OK;
        public int FinishOperations(int hrResult) => S_OK;
        public int PreRenameItem(uint dwFlags, IntPtr psiItem, IntPtr pszNewName) => Veto();
        public int PostRenameItem(uint dwFlags, IntPtr psiItem, IntPtr pszNewName, int hrRename, IntPtr psiNewlyCreated) => S_OK;
        public int PreMoveItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName) => Veto();
        public int PostMoveItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrMove, IntPtr psiNewlyCreated) => S_OK;
        public int PreCopyItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName) => Veto();
        public int PostCopyItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrCopy, IntPtr psiNewlyCreated) => S_OK;
        public int PreNewItem(uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName) => Veto();
        public int PostNewItem(uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pszTemplateName, uint dwFileAttributes, int hrNew, IntPtr psiNewItem) => S_OK;
        public int UpdateProgress(uint iWorkTotal, uint iWorkSoFar) => S_OK;
        public int ResetTimer() => S_OK;
        public int PauseTimer() => S_OK;
        public int ResumeTimer() => S_OK;
    }

    // Vtable order below must match shobjidl_core.h exactly. Unused parameters are IntPtr so nothing is marshalled.

    [ComImport, Guid("947AAB5F-0A5C-4C13-B4D6-4BF7836FC9F8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperation
    {
        [PreserveSig] int Advise(IFileOperationProgressSink pfops, out uint pdwCookie);
        [PreserveSig] int Unadvise(uint dwCookie);
        [PreserveSig] int SetOperationFlags(uint dwOperationFlags);
        [PreserveSig] int SetProgressMessage(IntPtr pszMessage);
        [PreserveSig] int SetProgressDialog(IntPtr popd);
        [PreserveSig] int SetProperties(IntPtr pproparray);
        [PreserveSig] int SetOwnerWindow(IntPtr hwndOwner);
        [PreserveSig] int ApplyPropertiesToItem(IntPtr psiItem);
        [PreserveSig] int ApplyPropertiesToItems(IntPtr punkItems);
        [PreserveSig] int RenameItem(IntPtr psiItem, IntPtr pszNewName, IntPtr pfopsItem);
        [PreserveSig] int RenameItems(IntPtr pUnkItems, IntPtr pszNewName);
        [PreserveSig] int MoveItem(IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pfopsItem);
        [PreserveSig] int MoveItems(IntPtr punkItems, IntPtr psiDestinationFolder);
        [PreserveSig] int CopyItem(IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszCopyName, IntPtr pfopsItem);
        [PreserveSig] int CopyItems(IntPtr punkItems, IntPtr psiDestinationFolder);
        [PreserveSig] int DeleteItem(IntPtr psiItem, IntPtr pfopsItem);
        [PreserveSig] int DeleteItems(IntPtr punkItems);
        [PreserveSig] int NewItem(IntPtr psiDestinationFolder, uint dwFileAttributes, IntPtr pszName, IntPtr pszTemplateName, IntPtr pfopsItem);
        [PreserveSig] int PerformOperations();
        [PreserveSig] int GetAnyOperationsAborted([MarshalAs(UnmanagedType.Bool)] out bool pfAnyOperationsAborted);
    }

    [ComImport, Guid("04b0f1a7-9490-44bc-96e1-4296a31252e2"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOperationProgressSink
    {
        [PreserveSig] int StartOperations();
        [PreserveSig] int FinishOperations(int hrResult);
        [PreserveSig] int PreRenameItem(uint dwFlags, IntPtr psiItem, IntPtr pszNewName);
        [PreserveSig] int PostRenameItem(uint dwFlags, IntPtr psiItem, IntPtr pszNewName, int hrRename, IntPtr psiNewlyCreated);
        [PreserveSig] int PreMoveItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName);
        [PreserveSig] int PostMoveItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrMove, IntPtr psiNewlyCreated);
        [PreserveSig] int PreCopyItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName);
        [PreserveSig] int PostCopyItem(uint dwFlags, IntPtr psiItem, IntPtr psiDestinationFolder, IntPtr pszNewName, int hrCopy, IntPtr psiNewlyCreated);
        [PreserveSig] int PreDeleteItem(uint dwFlags, IntPtr psiItem);
        [PreserveSig] int PostDeleteItem(uint dwFlags, IntPtr psiItem, int hrDelete, IntPtr psiNewlyCreated);
        [PreserveSig] int PreNewItem(uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName);
        [PreserveSig] int PostNewItem(uint dwFlags, IntPtr psiDestinationFolder, IntPtr pszNewName, IntPtr pszTemplateName, uint dwFileAttributes, int hrNew, IntPtr psiNewItem);
        [PreserveSig] int UpdateProgress(uint iWorkTotal, uint iWorkSoFar);
        [PreserveSig] int ResetTimer();
        [PreserveSig] int PauseTimer();
        [PreserveSig] int ResumeTimer();
    }

    [ComImport, Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        [PreserveSig] int BindToHandler(IntPtr pbc, IntPtr bhid, IntPtr riid, out IntPtr ppv);
        [PreserveSig] int GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        [PreserveSig] int GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        [PreserveSig] int Compare(IntPtr psi, uint hint, out int piOrder);
    }
}
