using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace WinScope.Modules.Events;

/// <summary>
/// Which physical disk holds Windows, so a disk error on it can be treated as serious. Opens the system volume
/// (e.g. \\.\C:) with no access rights – allowed for standard users – and asks for its device number.
/// </summary>
internal static class SystemDisk
{
    private const uint FILE_SHARE_READ = 0x1, FILE_SHARE_WRITE = 0x2, OPEN_EXISTING = 3;
    // CTL_CODE(IOCTL_STORAGE_BASE = 0x2D, 0x0420, METHOD_BUFFERED, FILE_ANY_ACCESS) = 0x002D1080 (winioctl.h)
    private const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
    // STORAGE_DEVICE_NUMBER { DEVICE_TYPE DeviceType @0; ULONG DeviceNumber @4; ULONG PartitionNumber @8 } = 12 bytes
    private const int StorageDeviceNumberSize = 12, DeviceNumberOffset = 4;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, IntPtr inBuffer, uint inSize,
        IntPtr outBuffer, uint outSize, out uint returned, IntPtr overlapped);

    /// <summary>Disk number of the Windows drive, or null (e.g. dynamic disks / Storage Spaces, or any failure).</summary>
    public static int? Number()
    {
        string? root = Path.GetPathRoot(Environment.SystemDirectory);           // "C:\"
        if (root is not { Length: >= 2 } || root[1] != ':' || !char.IsAsciiLetter(root[0])) return null;
        using var h = CreateFile(@"\\.\" + root[..2], 0, FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) return null;
        IntPtr buf = Marshal.AllocHGlobal(StorageDeviceNumberSize);
        try
        {
            if (!DeviceIoControl(h, IOCTL_STORAGE_GET_DEVICE_NUMBER, IntPtr.Zero, 0, buf, StorageDeviceNumberSize, out uint got, IntPtr.Zero)) return null;
            if (got < DeviceNumberOffset + 4) return null;
            int n = Marshal.ReadInt32(buf, DeviceNumberOffset);
            return n is >= 0 and < 1000 ? n : null;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }
}
