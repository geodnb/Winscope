using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace WinScope.Modules.Performance;

/// <summary>One process from a SystemProcessInformation snapshot (numbers only; the name is read on demand).</summary>
internal struct RawProc
{
    public long Pid;
    public long CreateTime;     // FILETIME (100 ns since 1601, UTC); 0 for Idle/System
    public long CpuTime;        // UserTime + KernelTime, 100 ns units
    public long PrivateWs;      // private working set, bytes (what Task Manager calls "Memory")
    public long WorkingSet;     // full working set, bytes
    public long IoBytes;        // ReadTransferCount + WriteTransferCount (all I/O: disk, network, pipes, devices)
    public int NameOffset;      // UTF-16 image name inside the copied buffer, or -1
    public int NameBytes;
}

/// <summary>
/// Parses the SYSTEM_PROCESS_INFORMATION chain returned by NtQuerySystemInformation(SystemProcessInformation).
/// The native block is copied into a managed byte[] first, so parsing is pure (unit-tested with synthetic buffers)
/// and every read is bounds-checked against the bytes Windows actually returned.
///
/// x64 layout (winternl.h documents the struct with "Reserved" names; the field names/offsets below are the
/// long-standing layout used by Process Hacker's phnt ntexapi.h and the Windows SDK's ntddk docs, Vista+):
///   0   ULONG          NextEntryOffset       (bytes to the next record; 0 = last)
///   4   ULONG          NumberOfThreads
///   8   LARGE_INTEGER  WorkingSetPrivateSize (Vista+)
///   16  ULONG          HardFaultCount
///   20  ULONG          NumberOfThreadsHighWatermark
///   24  ULONGLONG      CycleTime
///   32  LARGE_INTEGER  CreateTime
///   40  LARGE_INTEGER  UserTime
///   48  LARGE_INTEGER  KernelTime
///   56  UNICODE_STRING ImageName  { USHORT Length @56, USHORT MaximumLength @58, (pad 4), PWSTR Buffer @64 }
///   72  KPRIORITY      BasePriority (LONG) + 4 pad
///   80  HANDLE         UniqueProcessId
///   88  HANDLE         InheritedFromUniqueProcessId
///   96  ULONG          HandleCount
///   100 ULONG          SessionId
///   104 ULONG_PTR      UniqueProcessKey
///   112 SIZE_T         PeakVirtualSize
///   120 SIZE_T         VirtualSize
///   128 ULONG          PageFaultCount + 4 pad
///   136 SIZE_T         PeakWorkingSetSize
///   144 SIZE_T         WorkingSetSize
///   152..207           quota / pagefile / PrivatePageCount (7 × SIZE_T)
///   208 LARGE_INTEGER  ReadOperationCount
///   216 LARGE_INTEGER  WriteOperationCount
///   224 LARGE_INTEGER  OtherOperationCount
///   232 LARGE_INTEGER  ReadTransferCount
///   240 LARGE_INTEGER  WriteTransferCount
///   248 LARGE_INTEGER  OtherTransferCount
///   256 SYSTEM_THREAD_INFORMATION Threads[NumberOfThreads] (80 bytes each; not read)
/// ImageName.Buffer is an absolute pointer into the same returned block.
/// </summary>
internal static class ProcessParser
{
    public const int EntrySize = 256;               // fixed part of SYSTEM_PROCESS_INFORMATION (x64)
    private const int MaxEntries = 65536;
    private const int MaxNameBytes = 1024;          // image names are file names (≤ 260 chars); anything bigger is junk

    public const int OffNext = 0, OffPrivateWs = 8, OffCreate = 32, OffUser = 40, OffKernel = 48,
        OffNameLen = 56, OffNameBuf = 64, OffPid = 80, OffWorkingSet = 144, OffReadXfer = 232, OffWriteXfer = 240;

    /// <summary>Parse <paramref name="buf"/> (a copy of the native block that started at <paramref name="baseAddress"/>).</summary>
    /// <summary>The offsets here are the x64 layout; on any other build the parsers refuse rather than misread.</summary>
    public static bool Supported => IntPtr.Size == 8;

    public static void Parse(ReadOnlySpan<byte> buf, ulong baseAddress, List<RawProc> output)
    {
        output.Clear();
        if (!Supported) return;
        long off = 0;
        for (int guard = 0; guard < MaxEntries; guard++)
        {
            if (off < 0 || off + EntrySize > buf.Length) break;            // truncated record
            var e = buf.Slice((int)off, EntrySize);
            uint next = U32(e, OffNext);
            var p = new RawProc
            {
                Pid = I64(e, OffPid),
                CreateTime = I64(e, OffCreate),
                CpuTime = Sat(I64(e, OffUser), I64(e, OffKernel)),
                PrivateWs = Math.Max(0, I64(e, OffPrivateWs)),
                WorkingSet = Math.Max(0, I64(e, OffWorkingSet)),
                IoBytes = Sat(I64(e, OffReadXfer), I64(e, OffWriteXfer)),
                NameOffset = -1,
            };
            int len = BinaryPrimitives.ReadUInt16LittleEndian(e.Slice(OffNameLen));
            ulong ptr = BinaryPrimitives.ReadUInt64LittleEndian(e.Slice(OffNameBuf));
            if (len > 0 && len <= MaxNameBytes && (len & 1) == 0 && ptr >= baseAddress)
            {
                ulong rel = ptr - baseAddress;
                if (rel <= (ulong)buf.Length && rel + (ulong)len <= (ulong)buf.Length)
                {
                    p.NameOffset = (int)rel;
                    p.NameBytes = len;
                }
            }
            output.Add(p);
            if (next == 0) break;
            if (next < EntrySize) break;                                    // malformed: records can't overlap
            off += next;
        }
    }

    /// <summary>The image name of <paramref name="p"/>, or null if it has none (Idle) or it was out of bounds.</summary>
    public static string? ReadName(ReadOnlySpan<byte> buf, in RawProc p)
    {
        if (p.NameOffset < 0 || p.NameBytes <= 0 || p.NameOffset + p.NameBytes > buf.Length) return null;
        var chars = MemoryMarshal.Cast<byte, char>(buf.Slice(p.NameOffset, p.NameBytes));
        int nul = chars.IndexOf('\0');
        if (nul >= 0) chars = chars[..nul];
        if (chars.IsEmpty) return null;
        return new string(chars);
    }

    private static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32LittleEndian(s.Slice(o));
    private static long I64(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadInt64LittleEndian(s.Slice(o));

    /// <summary>a + b for non-negative counters, saturating instead of overflowing on junk values.</summary>
    private static long Sat(long a, long b)
    {
        if (a < 0) a = 0;
        if (b < 0) b = 0;
        return a > long.MaxValue - b ? long.MaxValue : a + b;
    }
}
