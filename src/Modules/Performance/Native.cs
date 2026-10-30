using System.Runtime.InteropServices;

namespace WinScope.Modules.Performance;

/// <summary>
/// P/Invoke declarations for the Performance tool. All of these are documented, user-mode APIs that work for a
/// standard (non-admin) user. DLLs resolve from System32 only (assembly-wide DefaultDllImportSearchPaths).
/// </summary>
internal static class Native
{
    // ------------------------------------------------------------------ kernel32

    /// <summary>
    /// GetSystemTimes(LPFILETIME idle, LPFILETIME kernel, LPFILETIME user). FILETIME is two DWORDs (low, high) =
    /// 8 bytes little-endian, so an out long is layout-identical. Kernel time INCLUDES idle time. Units: 100 ns,
    /// summed over all logical processors.
    /// </summary>
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetSystemTimes(out long idleTime, out long kernelTime, out long userTime);

    /// <summary>
    /// MEMORYSTATUSEX (sysinfoapi.h), 64 bytes on x86 and x64:
    ///  0 dwLength · 4 dwMemoryLoad · 8 ullTotalPhys · 16 ullAvailPhys · 24 ullTotalPageFile (= commit limit)
    ///  32 ullAvailPageFile · 40 ullTotalVirtual · 48 ullAvailVirtual · 56 ullAvailExtendedVirtual
    /// dwLength must be set to 64 before the call.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>GetActiveProcessorCount(ALL_PROCESSOR_GROUPS) – logical CPUs across all processor groups (Win7+).</summary>
    [DllImport("kernel32.dll")]
    public static extern uint GetActiveProcessorCount(ushort groupNumber);
    public const ushort ALL_PROCESSOR_GROUPS = 0xFFFF;

    // ------------------------------------------------------------------ ntdll

    /// <summary>
    /// NTSTATUS NtQuerySystemInformation(SYSTEM_INFORMATION_CLASS, PVOID, ULONG, PULONG). Documented in winternl.h.
    /// Class 5 = SystemProcessInformation: a chain of SYSTEM_PROCESS_INFORMATION records (layout in ProcessParser).
    /// Works as a standard user and needs no process handles, so protected/elevated processes are included too.
    /// </summary>
    [DllImport("ntdll.dll")]
    public static extern int NtQuerySystemInformation(int systemInformationClass, IntPtr systemInformation,
        int systemInformationLength, out int returnLength);

    public const int SystemProcessInformation = 5;
    public const int STATUS_SUCCESS = 0;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
    public const int STATUS_BUFFER_TOO_SMALL = unchecked((int)0xC0000023);

    // ------------------------------------------------------------------ pdh

    // PDH_STATUS is a LONG; declared as uint so the 0x8/0xC… codes compare naturally.
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint PdhOpenQueryW(string? szDataSource, IntPtr dwUserData, out IntPtr phQuery);

    /// <summary>Adds a counter by its ENGLISH path, so it works on non-English Windows too (Vista+).</summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint PdhAddEnglishCounterW(IntPtr hQuery, string szFullCounterPath, IntPtr dwUserData, out IntPtr phCounter);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhCollectQueryData(IntPtr hQuery);

    /// <summary>
    /// lpdwBufferSize is in BYTES. The buffer receives an array of PDH_FMT_COUNTERVALUE_ITEM_W followed by the
    /// instance-name strings the items point to (layout in PdhParser).
    /// </summary>
    [DllImport("pdh.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    public static extern uint PdhGetFormattedCounterArrayW(IntPtr hCounter, uint dwFormat, ref uint lpdwBufferSize,
        out uint lpdwItemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll", ExactSpelling = true)]
    public static extern uint PdhCloseQuery(IntPtr hQuery);

    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_NO_DATA = 0x800007D5;
    public const uint PDH_INVALID_DATA = 0xC0000BC6;
    public const uint PDH_FMT_DOUBLE = 0x00000200;
    public const uint PDH_FMT_NOCAP100 = 0x00008000;
    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;
}
