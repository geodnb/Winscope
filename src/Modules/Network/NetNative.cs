using System.Runtime.InteropServices;

namespace WinScope.Modules.Network;

/// <summary>
/// P/Invoke declarations for the Network tool. Everything here is plain Win32 from System32 (the assembly-wide
/// DefaultDllImportSearchPaths keeps loading to System32). Structures are never marshalled: callers read native
/// buffers by explicit x64 offsets with bounds checks (AllowUnsafeBlocks is off). The offsets used by callers were
/// cross-checked against Marshal.OffsetOf/SizeOf of equivalent sequential structs (see the report).
/// </summary>
internal static class NetNative
{
    public const uint NO_ERROR = 0;
    public const uint ERROR_ACCESS_DENIED = 5;
    public const uint ERROR_BAD_LENGTH = 24;
    public const uint ERROR_INVALID_PARAMETER = 87;
    public const uint ERROR_INSUFFICIENT_BUFFER = 122;
    public const uint ERROR_ALREADY_EXISTS = 183;
    public const uint ERROR_MORE_DATA = 234;
    public const uint ERROR_CANCELLED = 1223;
    public const uint ERROR_NO_SYSTEM_RESOURCES = 1450;
    public const uint ERROR_WMI_INSTANCE_NOT_FOUND = 4201;
    public const uint ERROR_CTX_CLOSE_PENDING = 7007;

    // ---------------------------------------------------------------- iphlpapi (no admin needed)

    public const int AF_INET = 2;
    public const int AF_INET6 = 23;
    /// <summary>TCP_TABLE_CLASS: BASIC_LISTENER 0, BASIC_CONNECTIONS 1, BASIC_ALL 2, OWNER_PID_LISTENER 3,
    /// OWNER_PID_CONNECTIONS 4, OWNER_PID_ALL 5 (iprtrmib.h).</summary>
    public const int TCP_TABLE_OWNER_PID_ALL = 5;
    /// <summary>UDP_TABLE_CLASS: BASIC 0, OWNER_PID 1, OWNER_MODULE 2.</summary>
    public const int UDP_TABLE_OWNER_PID = 1;

    /// <summary>DWORD GetExtendedTcpTable(PVOID, PDWORD, BOOL, ULONG, TCP_TABLE_CLASS, ULONG). Returns the error code
    /// directly (doesn't use SetLastError).</summary>
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, int reserved);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern uint GetExtendedUdpTable(IntPtr pUdpTable, ref int pdwSize, [MarshalAs(UnmanagedType.Bool)] bool bOrder,
        int ulAf, int tableClass, int reserved);

    /// <summary>NETIO_STATUS GetIfTable2(PMIB_IF_TABLE2*). The table is allocated by Windows; free with FreeMibTable.</summary>
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern uint GetIfTable2(out IntPtr table);

    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    public static extern void FreeMibTable(IntPtr memory);

    // ---------------------------------------------------------------- kernel32 (process names)

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    public static extern IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint pid);

    /// <summary>BOOL QueryFullProcessImageNameW(HANDLE, DWORD dwFlags, LPWSTR, PDWORD lpdwSize) – size in characters,
    /// in: buffer length, out: characters written (without the terminator).</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryFullProcessImageName(IntPtr process, uint flags, [Out] char[] exeName, ref int size);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseHandle(IntPtr handle);

    /// <summary>BOOL GetProcessTimes(HANDLE, LPFILETIME creation, exit, kernel, user) – FILETIMEs as 64-bit UTC ticks
    /// since 1601. Works with PROCESS_QUERY_LIMITED_INFORMATION.</summary>
    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetProcessTimes(IntPtr process, out long creation, out long exit, out long kernel, out long user);

    // ---------------------------------------------------------------- advapi32 ETW (admin only)

    public const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    public const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    public const uint EVENT_TRACE_CONTROL_STOP = 1;
    public const uint EVENT_CONTROL_CODE_ENABLE_PROVIDER = 1;
    public const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    public const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    /// <summary>(TRACEHANDLE)INVALID_HANDLE_VALUE on 64-bit.</summary>
    public const ulong INVALID_PROCESSTRACE_HANDLE = 0xFFFFFFFFFFFFFFFF;

    /// <summary>ULONG StartTraceW(PTRACEHANDLE, LPCWSTR, PEVENT_TRACE_PROPERTIES).</summary>
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "StartTraceW")]
    public static extern uint StartTrace(out ulong sessionHandle, string sessionName, IntPtr properties);

    /// <summary>ULONG ControlTraceW(TRACEHANDLE, LPCWSTR, PEVENT_TRACE_PROPERTIES, ULONG). Handle 0 + name = by name.</summary>
    [DllImport("advapi32.dll", ExactSpelling = true, CharSet = CharSet.Unicode, EntryPoint = "ControlTraceW")]
    public static extern uint ControlTrace(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    /// <summary>ULONG EnableTraceEx2(TRACEHANDLE, LPCGUID, ULONG ControlCode, UCHAR Level, ULONGLONG MatchAnyKeyword,
    /// ULONGLONG MatchAllKeyword, ULONG Timeout, PENABLE_TRACE_PARAMETERS).</summary>
    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern uint EnableTraceEx2(ulong sessionHandle, ref Guid providerId, uint controlCode, byte level,
        ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    /// <summary>TRACEHANDLE OpenTraceW(PEVENT_TRACE_LOGFILEW). INVALID_PROCESSTRACE_HANDLE on failure (GetLastError).</summary>
    [DllImport("advapi32.dll", ExactSpelling = true, SetLastError = true, EntryPoint = "OpenTraceW")]
    public static extern ulong OpenTrace(IntPtr logfile);

    /// <summary>ULONG ProcessTrace(PTRACEHANDLE HandleArray, ULONG HandleCount, LPFILETIME StartTime, LPFILETIME EndTime).
    /// Blocks until the session stops or the handle is closed.</summary>
    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern uint ProcessTrace([In] ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll", ExactSpelling = true)]
    public static extern uint CloseTrace(ulong traceHandle);

    /// <summary>VOID WINAPI EventRecordCallback(PEVENT_RECORD).</summary>
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    public delegate void EventRecordCallback(IntPtr eventRecord);
}
