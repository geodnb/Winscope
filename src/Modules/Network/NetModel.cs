using System.Net;
using System.Net.Sockets;

namespace WinScope.Modules.Network;

public enum Proto : byte { Tcp, Udp }

/// <summary>MIB_TCP_STATE values (tcpmib.h).</summary>
public enum TcpState
{
    None = 0, Closed = 1, Listen = 2, SynSent = 3, SynRcvd = 4, Established = 5, FinWait1 = 6, FinWait2 = 7,
    CloseWait = 8, Closing = 9, LastAck = 10, TimeWait = 11, DeleteTcb = 12
}

/// <summary>Where the other end is (or, for listening/UDP sockets, which networks the socket accepts from).</summary>
public enum Where : byte { None, Local, Lan, Internet, AnyNetwork, Shared }

/// <summary>One row from the TCP/UDP tables. Immutable; recreated on every 2 s refresh.</summary>
public sealed class ConnInfo
{
    public Proto Proto { get; }
    public IPAddress Local { get; }
    public int LocalPort { get; }
    /// <summary>Null for UDP endpoints (the UDP table only has the local side).</summary>
    public IPAddress? Remote { get; }
    public int RemotePort { get; }
    public TcpState State { get; }
    public uint Pid { get; }
    public Where Where { get; }

    public ConnInfo(Proto proto, IPAddress local, int localPort, IPAddress? remote, int remotePort, TcpState state, uint pid)
    {
        Proto = proto; Local = local; LocalPort = localPort; Remote = remote; RemotePort = remotePort; State = state; Pid = pid;
        var remoteClass = remote != null && proto == Proto.Tcp && state != TcpState.Listen ? AddressClass.Classify(remote) : Where.None;
        HasRemote = remoteClass != Where.None;
        Where = HasRemote ? remoteClass : AddressClass.BindScope(local);
    }

    public bool IsListening => Proto == Proto.Tcp && State == TcpState.Listen;
    /// <summary>A TCP connection with a real remote end (not listening, not 0.0.0.0:0).</summary>
    public bool HasRemote { get; }

    private string? _remoteText, _key;
    /// <summary>"93.184.216.34:443" or "[2606:2800::1]:443"; "—" when there's no remote end.</summary>
    public string RemoteText => _remoteText ??= HasRemote ? AddressClass.EndPoint(Remote!, RemotePort) : "—";
    public string LocalText => AddressClass.EndPoint(Local, LocalPort);
    /// <summary>Identity for diffing UI rows (includes state, so a state change is a remove + add).</summary>
    public string Key => _key ??= $"{(Proto == Proto.Tcp ? 'T' : 'U')}|{Local}|{LocalPort}|{Remote}|{RemotePort}|{(int)State}|{Pid}";
}

public static class AddressClass
{
    /// <summary>Local (loopback), LAN (private, link-local, unique-local, multicast) or Internet. None = unspecified.</summary>
    public static Where Classify(IPAddress a)
    {
        Span<byte> b = stackalloc byte[16];
        if (!a.TryWriteBytes(b, out int n)) return Where.Internet;
        if (n == 4) return ClassifyV4(b[0], b[1], b[2], b[3]);
        if (n != 16) return Where.Internet;
        bool allZero = true;
        for (int i = 0; i < 15; i++) if (b[i] != 0) { allZero = false; break; }
        if (allZero) return b[15] == 0 ? Where.None : b[15] == 1 ? Where.Local : Where.Internet;   // :: / ::1
        // ::ffff:a.b.c.d (IPv4-mapped)
        bool mapped = b[10] == 0xFF && b[11] == 0xFF;
        for (int i = 0; i < 10 && mapped; i++) if (b[i] != 0) mapped = false;
        if (mapped) return ClassifyV4(b[12], b[13], b[14], b[15]);
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0x80) return Where.Lan;   // fe80::/10 link-local
        if (b[0] == 0xFE && (b[1] & 0xC0) == 0xC0) return Where.Lan;   // fec0::/10 site-local (deprecated, still seen on some LANs)
        if ((b[0] & 0xFE) == 0xFC) return Where.Lan;                   // fc00::/7 unique local
        if (b[0] == 0xFF) return Where.Lan;                            // ff00::/8 multicast
        return Where.Internet;
    }

    public static Where ClassifyV4(byte a, byte b, byte c, byte d)
    {
        if (a == 0) return (b | c | d) == 0 ? Where.None : Where.Lan;   // 0.0.0.0 unspecified / 0.x "this network"
        if (a == 127) return Where.Local;
        if (a == 10) return Where.Lan;
        if (a == 172 && (b & 0xF0) == 16) return Where.Lan;     // 172.16.0.0/12
        if (a == 192 && b == 168) return Where.Lan;
        if (a == 169 && b == 254) return Where.Lan;             // link-local (APIPA)
        if (a == 100 && (b & 0xC0) == 64) return Where.Shared;  // 100.64.0.0/10 carrier-grade NAT / Tailscale-style VPNs
        if (a >= 224) return Where.Lan;                         // multicast, broadcast, reserved
        return Where.Internet;
    }

    /// <summary>For a listening/UDP socket: which networks can reach it, from the address it's bound to.</summary>
    public static Where BindScope(IPAddress local)
    {
        var w = Classify(local);
        return w == Where.None ? Where.AnyNetwork : w;
    }

    public static string EndPoint(IPAddress a, int port) =>
        a.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{a}]:{port}" : $"{a}:{port}";

    public static string Label(Where w) => w switch
    {
        Where.Local => "Local",
        Where.Lan => "LAN",
        Where.Internet => "Internet",
        Where.AnyNetwork => "Any network",
        Where.Shared => "Shared/VPN",
        _ => "—"
    };

    public static string Explain(Where w, bool bound) => (w, bound) switch
    {
        (Where.Local, false) => "Another program on this PC",
        (Where.Local, true) => "Only programs on this PC can reach it",
        (Where.Lan, false) => "A device on your local network (or a local broadcast)",
        (Where.Lan, true) => "Bound to a local-network address",
        (Where.Internet, false) => "A server on the internet",
        (Where.Internet, true) => "Bound to a public address",
        (Where.Shared, false) => "A shared address (100.64.0.0/10): your provider's carrier-grade NAT, or a VPN such as Tailscale",
        (Where.Shared, true) => "Bound to a shared/VPN address (100.64.0.0/10)",
        (Where.AnyNetwork, _) => "Accepts traffic on every network this PC is on (if the firewall allows it)",
        _ => ""
    };

    /// <summary>Plain-English TCP state.</summary>
    public static string StateText(ConnInfo c) => c.Proto == Proto.Udp ? "—" : c.State switch
    {
        TcpState.Established => "Established",
        TcpState.Listen => "Listening",
        TcpState.SynSent or TcpState.SynRcvd => "Connecting",
        TcpState.FinWait1 or TcpState.FinWait2 or TcpState.CloseWait or TcpState.Closing or TcpState.LastAck => "Closing",
        TcpState.TimeWait => "Time wait",
        TcpState.Closed or TcpState.DeleteTcb => "Closed",
        _ => "?"
    };

    public static int StateOrder(ConnInfo c) => c.Proto == Proto.Udp ? 6 : c.State switch
    {
        TcpState.Established => 0,
        TcpState.SynSent or TcpState.SynRcvd => 1,
        TcpState.Listen => 2,
        TcpState.TimeWait => 4,
        TcpState.Closed or TcpState.DeleteTcb => 5,
        _ => 3
    };
}

/// <summary>
/// Monotonic "UTC" for the history and graph: anchored to the wall clock once, then advanced by Stopwatch, so a
/// backward clock correction (NTP, manual change) can never freeze or reorder the graph. If the wall clock gets
/// far ahead (e.g. Stopwatch didn't advance across sleep on some machine), it re-anchors forward – forward jumps just
/// show as a gap.
/// </summary>
public static class MonoClock
{
    private static readonly object Gate = new();
    private static long _anchorUtcTicks = DateTime.UtcNow.Ticks;
    private static long _anchorTs = System.Diagnostics.Stopwatch.GetTimestamp();
    private static long _last;

    public static DateTime UtcNow
    {
        get
        {
            lock (Gate)
            {
                long t = _anchorUtcTicks + (long)((System.Diagnostics.Stopwatch.GetTimestamp() - _anchorTs) *
                    (TimeSpan.TicksPerSecond / (double)System.Diagnostics.Stopwatch.Frequency));
                long wall = DateTime.UtcNow.Ticks;
                if (wall - t > TimeSpan.TicksPerMinute * 2) { _anchorUtcTicks = wall; _anchorTs = System.Diagnostics.Stopwatch.GetTimestamp(); t = wall; }
                if (t < _last) t = _last;
                _last = t;
                return new DateTime(t, DateTimeKind.Utc);
            }
        }
    }
}

/// <summary>One second of total traffic across the physical adapters (time from <see cref="MonoClock"/>).</summary>
public readonly record struct RateSample(long Ticks, float Rx, float Tx)
{
    public DateTime Utc => new(Ticks, DateTimeKind.Utc);
}

/// <summary>Fixed-size ring of 1 s samples (30 min + slack). Thread-safe.</summary>
public sealed class RateHistory
{
    private readonly RateSample[] _buf;
    private int _start, _count;
    private readonly object _lock = new();

    public RateHistory(int capacity) => _buf = new RateSample[Math.Max(4, capacity)];

    public int Count { get { lock (_lock) return _count; } }

    public void Add(DateTime utc, double rx, double tx)
    {
        var s = new RateSample(utc.Ticks, (float)Math.Max(0, rx), (float)Math.Max(0, tx));
        lock (_lock)
        {
            if (_count > 0 && _buf[(_start + _count - 1) % _buf.Length].Ticks > s.Ticks) return;   // never happens with MonoClock; defensive
            if (_count < _buf.Length) { _buf[(_start + _count) % _buf.Length] = s; _count++; }
            else { _buf[_start] = s; _start = (_start + 1) % _buf.Length; }
        }
    }

    /// <summary>Samples at or after <paramref name="utc"/>, oldest first.</summary>
    public void CopySince(DateTime utc, List<RateSample> into)
    {
        into.Clear();
        long t = utc.Ticks;
        lock (_lock)
        {
            // binary search the first sample >= t
            int lo = 0, hi = _count;
            while (lo < hi) { int mid = (lo + hi) / 2; if (_buf[(_start + mid) % _buf.Length].Ticks < t) lo = mid + 1; else hi = mid; }
            for (int i = lo; i < _count; i++) into.Add(_buf[(_start + i) % _buf.Length]);
        }
    }

    /// <summary>Copy the newest <paramref name="n"/> samples (oldest first) into the arrays; returns how many.</summary>
    public int CopyLast(int n, double[] rx, double[] tx)
    {
        lock (_lock)
        {
            int k = Math.Min(Math.Min(n, _count), Math.Min(rx.Length, tx.Length));
            for (int i = 0; i < k; i++)
            {
                var s = _buf[(_start + _count - k + i) % _buf.Length];
                rx[i] = s.Rx; tx[i] = s.Tx;
            }
            return k;
        }
    }
}

/// <summary>Per-app figures for one snapshot.</summary>
public sealed class AppStat
{
    public required string Key { get; init; }
    public required string Name { get; init; }
    public string? Path { get; init; }
    public int ProcCount { get; init; }
    public string PidList { get; init; } = "";
    /// <summary>Bytes/s (admin only; 0 otherwise).</summary>
    public double Down { get; init; }
    public double Up { get; init; }
    public long TotalDown { get; init; }
    public long TotalUp { get; init; }
    public int Established { get; init; }
    public int ListeningPorts { get; init; }
    public int Udp { get; init; }
    public int TcpAll { get; init; }
    public required ConnInfo[] Conns { get; init; }
}

public sealed class NetSnapshot
{
    public DateTime Utc { get; init; }
    public double RxRate { get; init; }
    public double TxRate { get; init; }
    public long SessionRx { get; init; }
    public long SessionTx { get; init; }
    public required IReadOnlyList<AppStat> Apps { get; init; }
    /// <summary>Increments whenever the connection tables were re-read.</summary>
    public int ConnVersion { get; init; }
    public int ConnCount { get; init; }
    /// <summary>Per-app speeds are being measured (admin + ETW running).</summary>
    public bool PerApp { get; init; }
    /// <summary>Why per-app speeds are off or degraded (admin only; null when fine or when not admin).</summary>
    public string? EtwMessage { get; init; }
    public string? ConnError { get; init; }
    public string AdapterNames { get; init; } = "";
}

/// <summary>Vertical budget of the dashboard card (pure maths, unit-tested): 3 apps at the 446 × 245 minimum, up to 5 when taller.</summary>
public static class SummaryLayout
{
    public const int MaxRows = 5;
    public const double RowH = 24, NumbersH = 46, HeaderH = 30, HintH = 22, SparkGap = 6, SparkMin = 36, SparkMax = 120;

    /// <summary>How many app rows fit, and how tall the sparkline can be, in a card body this tall.</summary>
    public static (int rows, double spark) Fit(double height, bool hint)
    {
        double avail = height - 4;   // a little slack for font metrics
        double fixedH = NumbersH + SparkGap + HeaderH + (hint ? HintH : 0);
        int rows = (int)Math.Clamp(Math.Floor((avail - fixedH - 56) / RowH), 0, MaxRows);
        if (rows == 0) fixedH -= HeaderH;   // no room for apps: drop their header too, give the space to the sparkline
        double spark = Math.Clamp(avail - fixedH - rows * RowH, SparkMin, SparkMax);
        return (rows, spark);
    }

    /// <summary>Total height the card's content needs for a given fit (to check nothing clips).</summary>
    public static double Needed(int rows, double spark, bool hint) =>
        NumbersH + SparkGap + spark + (rows > 0 ? HeaderH + rows * RowH : 0) + (hint ? HintH : 0);
}

/// <summary>Error sink with de-duplication, so a failure repeating every second doesn't flood error.log.</summary>
internal static class NetLog
{
    public static Action<Exception>? Sink;
    private static readonly Dictionary<string, long> Last = new();
    private static readonly object Gate = new();

    public static void Error(Exception ex)
    {
        try
        {
            string k = ex.GetType().FullName + ":" + ex.Message;
            long now = Environment.TickCount64;
            lock (Gate)
            {
                if (Last.TryGetValue(k, out var t) && now - t < 600_000) return;
                if (Last.Count > 200) Last.Clear();
                Last[k] = now;
            }
            Sink?.Invoke(ex);
        }
        catch { }
    }
}
