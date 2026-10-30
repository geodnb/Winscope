using System.Net;
using System.Runtime.InteropServices;
using static WinScope.Modules.Network.NetNative;

namespace WinScope.Modules.Network;

/// <summary>
/// Reads the TCP and UDP endpoint tables with their owning process ids (no admin needed). Every buffer is
/// treated as untrusted: the entry count is clamped to what actually fits in the bytes Windows filled.
///
/// Table layouts (iprtrmib.h / tcpmib.h / udpmib.h). All members are DWORDs or byte arrays, so the tables are
/// 4-byte aligned and the rows start right after the DWORD dwNumEntries at offset 4, on x86 and x64 alike:
///   MIB_TCPROW_OWNER_PID   (24 bytes): dwState 0, dwLocalAddr 4, dwLocalPort 8, dwRemoteAddr 12, dwRemotePort 16, dwOwningPid 20
///   MIB_TCP6ROW_OWNER_PID  (56 bytes): ucLocalAddr[16] 0, dwLocalScopeId 16, dwLocalPort 20, ucRemoteAddr[16] 24,
///                                      dwRemoteScopeId 40, dwRemotePort 44, dwState 48, dwOwningPid 52
///   MIB_UDPROW_OWNER_PID   (12 bytes): dwLocalAddr 0, dwLocalPort 4, dwOwningPid 8
///   MIB_UDP6ROW_OWNER_PID  (28 bytes): ucLocalAddr[16] 0, dwLocalScopeId 16, dwLocalPort 20, dwOwningPid 24
/// (sizes/offsets verified with Marshal.SizeOf/OffsetOf on equivalent sequential structs.)
/// IPv4 addresses are in network byte order (memory order a.b.c.d). Ports are in network byte order in the low
/// 16 bits of the DWORD; the upper 16 bits are undefined, so they're masked off.
/// </summary>
internal static class ConnectionTables
{
    public const int Header = 4;
    public const int Tcp4Row = 24, Tcp6Row = 56, Udp4Row = 12, Udp6Row = 28;
    private const int MaxRows = 250_000;
    private const int MaxBuffer = 64 * 1024 * 1024;

    /// <summary>Read all four tables. Returns null on success, or a short description of what failed (partial results kept).</summary>
    public static string? ReadAll(List<ConnInfo> into)
    {
        into.Clear();
        string? err = null;
        err ??= Query(true, AF_INET, ParseTcp4, into);
        var e2 = Query(true, AF_INET6, ParseTcp6, into); err ??= e2;
        var e3 = Query(false, AF_INET, ParseUdp4, into); err ??= e3;
        var e4 = Query(false, AF_INET6, ParseUdp6, into); err ??= e4;
        return err;
    }

    private static string? Query(bool tcp, int af, Action<IntPtr, int, List<ConnInfo>> parse, List<ConnInfo> into)
    {
        int size = 0;
        IntPtr buf = IntPtr.Zero;
        try
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                int asked = size;
                uint rc = tcp
                    ? GetExtendedTcpTable(buf, ref size, false, af, TCP_TABLE_OWNER_PID_ALL, 0)
                    : GetExtendedUdpTable(buf, ref size, false, af, UDP_TABLE_OWNER_PID, 0);
                if (rc == NO_ERROR)
                {
                    // On success Windows filled at most the buffer we passed; never parse beyond it.
                    if (buf != IntPtr.Zero) parse(buf, asked, into);
                    return null;
                }
                if (rc != ERROR_INSUFFICIENT_BUFFER) return $"{(tcp ? "TCP" : "UDP")}{(af == AF_INET6 ? "v6" : "v4")} table error {rc}";
                // The table can grow between calls: add some slack.
                if (size <= 0 || size > MaxBuffer) return "connection table too large";
                size = Math.Min(MaxBuffer, size + size / 4 + 1024);
                if (buf != IntPtr.Zero) { Marshal.FreeHGlobal(buf); buf = IntPtr.Zero; }
                buf = Marshal.AllocHGlobal(size);
            }
            return "connection table kept growing";
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Entries that really fit in <paramref name="len"/> bytes, capped.</summary>
    internal static int RowCount(IntPtr buf, int len, int rowSize)
    {
        if (buf == IntPtr.Zero || len < Header) return 0;
        long n = (uint)Marshal.ReadInt32(buf, 0);
        long fit = (len - Header) / rowSize;
        return (int)Math.Min(Math.Min(n, fit), MaxRows);
    }

    internal static int Port(int raw) => ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);

    private static IPAddress V4(IntPtr p, int off) => new((uint)Marshal.ReadInt32(p, off));   // IPAddress(long) takes network order as laid out in memory

    private static IPAddress V6(IntPtr p, int off)
    {
        var b = new byte[16];
        Marshal.Copy(p + off, b, 0, 16);
        return new IPAddress(b);   // scope ids dropped: shorter text, and they don't change the classification
    }

    private static TcpState State(int raw) => raw is >= 1 and <= 12 ? (TcpState)raw : TcpState.None;

    internal static void ParseTcp4(IntPtr buf, int len, List<ConnInfo> into)
    {
        int n = RowCount(buf, len, Tcp4Row);
        for (int i = 0; i < n; i++)
        {
            int o = Header + i * Tcp4Row;
            into.Add(new ConnInfo(Proto.Tcp,
                V4(buf, o + 4), Port(Marshal.ReadInt32(buf, o + 8)),
                V4(buf, o + 12), Port(Marshal.ReadInt32(buf, o + 16)),
                State(Marshal.ReadInt32(buf, o)), (uint)Marshal.ReadInt32(buf, o + 20)));
        }
    }

    internal static void ParseTcp6(IntPtr buf, int len, List<ConnInfo> into)
    {
        int n = RowCount(buf, len, Tcp6Row);
        for (int i = 0; i < n; i++)
        {
            int o = Header + i * Tcp6Row;
            into.Add(new ConnInfo(Proto.Tcp,
                V6(buf, o), Port(Marshal.ReadInt32(buf, o + 20)),
                V6(buf, o + 24), Port(Marshal.ReadInt32(buf, o + 44)),
                State(Marshal.ReadInt32(buf, o + 48)), (uint)Marshal.ReadInt32(buf, o + 52)));
        }
    }

    internal static void ParseUdp4(IntPtr buf, int len, List<ConnInfo> into)
    {
        int n = RowCount(buf, len, Udp4Row);
        for (int i = 0; i < n; i++)
        {
            int o = Header + i * Udp4Row;
            into.Add(new ConnInfo(Proto.Udp, V4(buf, o), Port(Marshal.ReadInt32(buf, o + 4)), null, 0, TcpState.None,
                (uint)Marshal.ReadInt32(buf, o + 8)));
        }
    }

    internal static void ParseUdp6(IntPtr buf, int len, List<ConnInfo> into)
    {
        int n = RowCount(buf, len, Udp6Row);
        for (int i = 0; i < n; i++)
        {
            int o = Header + i * Udp6Row;
            into.Add(new ConnInfo(Proto.Udp, V6(buf, o), Port(Marshal.ReadInt32(buf, o + 20)), null, 0, TcpState.None,
                (uint)Marshal.ReadInt32(buf, o + 24)));
        }
    }
}
