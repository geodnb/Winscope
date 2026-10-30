using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using static WinScope.Modules.Network.NetNative;

namespace WinScope.Modules.Network;

/// <summary>
/// Total bytes in/out across the PC's real network adapters (Wi-Fi, Ethernet, mobile broadband, USB tethering),
/// from GetIfTable2. Loopback, tunnels, virtual switches/VPN adapters and NDIS filter layers are left out so
/// traffic isn't counted twice. Falls back to System.Net.NetworkInformation if the table looks wrong.
///
/// MIB_IF_ROW2 (netioapi.h / ifdef.h), x64 and x86 identical (NET_LUID makes the row 8-aligned), 1352 bytes:
///   InterfaceLuid 0 (8), InterfaceIndex 8, InterfaceGuid 12 (16), Alias 28 (WCHAR[257]), Description 542 (WCHAR[257]),
///   PhysicalAddressLength 1056, PhysicalAddress 1060 (32), PermanentPhysicalAddress 1092 (32), Mtu 1124, Type 1128,
///   TunnelType 1132, MediaType 1136, PhysicalMediumType 1140, AccessType 1144, DirectionType 1148,
///   InterfaceAndOperStatusFlags 1152 (1 byte bitfield: bit0 HardwareInterface, bit1 FilterInterface, bit2 ConnectorPresent…),
///   OperStatus 1156, AdminStatus 1160, MediaConnectState 1164, NetworkGuid 1168 (16), ConnectionType 1184, (pad 4),
///   TransmitLinkSpeed 1192, ReceiveLinkSpeed 1200, InOctets 1208, … OutOctets 1280, … OutQLen 1344.
/// MIB_IF_TABLE2: ULONG NumEntries at 0, Table[] at 8.
/// (verified with Marshal.SizeOf/OffsetOf on an equivalent sequential struct.)
/// </summary>
internal sealed class AdapterCounters
{
    private const int RowSize = 1352, TableHeader = 8, MaxRows = 1024;
    private const int OffLuid = 0, OffGuid = 12, OffAlias = 28, OffDescription = 542, OffType = 1128, OffPhysMedium = 1140,
        OffFlags = 1152, OffOper = 1156, OffTxLink = 1192, OffRxLink = 1200, OffInOctets = 1208, OffOutOctets = 1280;
    private const int IF_TYPE_SOFTWARE_LOOPBACK = 24, IF_TYPE_TUNNEL = 131;
    private const int IfOperStatusUp = 1;
    private const ulong MaxPlausibleDelta = 200UL * 1024 * 1024 * 1024;   // 200 GB in one tick = counter glitch

    private readonly Dictionary<ulong, (ulong rx, ulong tx)> _prev = new();
    private readonly Dictionary<string, (long rx, long tx)> _prevFallback = new();
    private readonly HashSet<ulong> _seen = new();
    private readonly List<ulong> _drop = new();
    private readonly List<IntPtr> _rows = new();
    private string _setSignature = "";
    private bool _validated;

    public bool UsingFallback { get; private set; }
    /// <summary>Friendly names of the adapters being counted, e.g. "Wi-Fi, Ethernet".</summary>
    public string AdapterNames { get; private set; } = "";
    /// <summary>The adapters being counted (immutable list, replaced when the set or a link speed changes).</summary>
    public IReadOnlyList<AdapterInfo> Adapters { get; private set; } = Array.Empty<AdapterInfo>();
    private readonly List<(long rx, long tx)> _speeds = new();

    /// <summary>Bytes received/sent since the previous call (0,0 on the first call).</summary>
    public (long rx, long tx) ReadDelta()
    {
        if (!UsingFallback)
        {
            try
            {
                var r = ReadTable();
                if (r is { } ok) return ok;
            }
            catch (Exception ex) { NetLog.Error(ex); }
            UsingFallback = true;   // table missing or implausible → managed API from now on
            _prev.Clear();
        }
        return ReadFallback();
    }

    private (long rx, long tx)? ReadTable()
    {
        if (GetIfTable2(out var table) != NO_ERROR || table == IntPtr.Zero) return null;
        try
        {
            long n = (uint)Marshal.ReadInt32(table, 0);
            if (n > MaxRows) return null;
            if (!_validated)
            {
                if (!Validate(table, (int)n)) return null;
                _validated = true;
            }
            long rx = 0, tx = 0;
            _seen.Clear();
            _rows.Clear();
            _speeds.Clear();
            var sig = new System.Text.StringBuilder();
            for (int i = 0; i < n; i++)
            {
                IntPtr row = table + TableHeader + i * RowSize;
                int type = Marshal.ReadInt32(row, OffType);
                byte flags = Marshal.ReadByte(row, OffFlags);
                int oper = Marshal.ReadInt32(row, OffOper);
                bool hardware = (flags & 0x01) != 0, filter = (flags & 0x02) != 0;
                if (!hardware || filter || oper != IfOperStatusUp || type == IF_TYPE_SOFTWARE_LOOPBACK || type == IF_TYPE_TUNNEL) continue;
                ulong luid = (ulong)Marshal.ReadInt64(row, OffLuid);
                ulong inO = (ulong)Marshal.ReadInt64(row, OffInOctets), outO = (ulong)Marshal.ReadInt64(row, OffOutOctets);
                if (!_seen.Add(luid)) continue;
                sig.Append(luid).Append(',');
                if (_prev.TryGetValue(luid, out var p))
                {
                    if (inO >= p.rx && inO - p.rx < MaxPlausibleDelta) rx += (long)(inO - p.rx);
                    if (outO >= p.tx && outO - p.tx < MaxPlausibleDelta) tx += (long)(outO - p.tx);
                }
                _prev[luid] = (inO, outO);
                _rows.Add(row);
                _speeds.Add((LinkSpeed(row, OffRxLink), LinkSpeed(row, OffTxLink)));
            }
            // Forget adapters that went away, so one coming back doesn't count its whole downtime as one jump.
            _drop.Clear();
            foreach (var k in _prev.Keys) if (!_seen.Contains(k)) _drop.Add(k);
            foreach (var k in _drop) _prev.Remove(k);
            string s = sig.ToString();
            if (s != _setSignature)
            {
                // Only re-read the names when the set of adapters changes (the table is still valid here).
                _setSignature = s;
                AdapterNames = string.Join(", ", _rows.Select(r => ReadWString(r + OffAlias, 257)).Where(x => x.Length > 0));
                Adapters = _rows.Select((r, i) => new AdapterInfo(ReadWString(r + OffAlias, 257), ReadWString(r + OffDescription, 257),
                    Kind(Marshal.ReadInt32(r, OffType), Marshal.ReadInt32(r, OffPhysMedium)), _speeds[i].rx, _speeds[i].tx)).ToArray();
            }
            else if (SpeedsChanged())
            {
                // Same adapters, new link rate (Wi-Fi rates move about): only copy the speeds.
                var old = Adapters;
                Adapters = old.Select((a, i) => i < _speeds.Count ? a with { RxLinkBps = _speeds[i].rx, TxLinkBps = _speeds[i].tx } : a).ToArray();
            }
            return (rx, tx);
        }
        finally { FreeMibTable(table); }
    }

    /// <summary>
    /// One-off sanity check of the row layout: OperStatus must be a valid IF_OPER_STATUS for every row, and the
    /// interface GUIDs at offset 12 must match the ids .NET reports for the same adapters.
    /// </summary>
    private static bool Validate(IntPtr table, int n)
    {
        if (n == 0) return true;
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try { foreach (var ni in NetworkInterface.GetAllNetworkInterfaces()) ids.Add(ni.Id); } catch { }
        int matched = 0;
        var gb = new byte[16];
        for (int i = 0; i < n; i++)
        {
            IntPtr row = table + TableHeader + i * RowSize;
            int oper = Marshal.ReadInt32(row, OffOper);
            if (oper is < 1 or > 7) return false;
            Marshal.Copy(row + OffGuid, gb, 0, 16);
            if (ids.Contains(new Guid(gb).ToString("B"))) matched++;
        }
        return ids.Count == 0 || matched > 0;
    }

    private bool SpeedsChanged()
    {
        var a = Adapters;
        if (a.Count != _speeds.Count) return false;
        for (int i = 0; i < a.Count; i++) if (a[i].RxLinkBps != _speeds[i].rx || a[i].TxLinkBps != _speeds[i].tx) return true;
        return false;
    }

    /// <summary>Link speed in bits/s; 0 when not reported (some drivers give 0 or all ones).</summary>
    private static long LinkSpeed(IntPtr row, int off)
    {
        ulong v = (ulong)Marshal.ReadInt64(row, off);
        return v is 0 or > 1_000_000_000_000UL ? 0 : (long)v;
    }

    /// <summary>IFTYPE (ipifcons.h): 6 Ethernet, 71 IEEE 802.11, 243/244 mobile broadband; NDIS physical medium
    /// 1 (wireless LAN) / 9 (native 802.11) also mean Wi-Fi.</summary>
    internal static string Kind(int ifType, int physMedium) => ifType switch
    {
        71 => "Wi-Fi",
        243 or 244 => "Mobile broadband",
        6 when physMedium is 1 or 9 => "Wi-Fi",
        6 => "Ethernet",
        _ => physMedium is 1 or 9 ? "Wi-Fi" : "Other"
    };

    private static string ReadWString(IntPtr p, int maxChars)
    {
        string s = Marshal.PtrToStringUni(p, maxChars);
        int z = s.IndexOf('\0');
        return (z >= 0 ? s[..z] : s).Trim();
    }

    private static readonly string[] VirtualHints =
        { "virtual", "hyper-v", "vmware", "virtualbox", "tap-", "wireguard", "wintun", "vpn", "loopback", "npcap", "pseudo", "miniport" };

    private (long rx, long tx) ReadFallback()
    {
        long rx = 0, tx = 0;
        var seen = new HashSet<string>();
        var names = new List<string>();
        var infos = new List<AdapterInfo>();
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                if (ni.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 or
                    NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT or NetworkInterfaceType.FastEthernetFx or
                    NetworkInterfaceType.Ethernet3Megabit or NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2)) continue;
                string d = (ni.Description + " " + ni.Name).ToLowerInvariant();
                if (VirtualHints.Any(d.Contains)) continue;
                var st = ni.GetIPStatistics();
                long inO = st.BytesReceived, outO = st.BytesSent;
                seen.Add(ni.Id);
                names.Add(ni.Name);
                long speed = ni.Speed is > 0 and < 1_000_000_000_000 ? ni.Speed : 0;
                string kind = ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? "Wi-Fi"
                    : ni.NetworkInterfaceType is NetworkInterfaceType.Wwanpp or NetworkInterfaceType.Wwanpp2 ? "Mobile broadband" : "Ethernet";
                infos.Add(new AdapterInfo(ni.Name, ni.Description, kind, speed, speed));
                if (_prevFallback.TryGetValue(ni.Id, out var p))
                {
                    if (inO >= p.rx) rx += inO - p.rx;
                    if (outO >= p.tx) tx += outO - p.tx;
                }
                _prevFallback[ni.Id] = (inO, outO);
            }
            catch (NetworkInformationException) { }
        }
        foreach (var k in _prevFallback.Keys.Where(k => !seen.Contains(k)).ToList()) _prevFallback.Remove(k);
        AdapterNames = string.Join(", ", names);
        Adapters = infos.ToArray();
        return (rx, tx);
    }
}
