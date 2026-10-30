using System.Runtime.InteropServices;
using static WinScope.Modules.Network.NetNative;

namespace WinScope.Modules.Network;

/// <summary>
/// The names of the Wi-Fi profiles saved on this PC (connected and past networks). Windows names a profile after
/// its network (SSID) by default, so a redacted report registers them with Redactor.NetworkName and they're masked
/// wherever they appear.
///
/// Only WlanEnumInterfaces + WlanGetProfileList are used. Neither is on Microsoft's list of Wi-Fi APIs gated by
/// location consent (learn.microsoft.com/windows/win32/nativewifi/wi-fi-access-location-changes: that list is
/// WlanGetAvailableNetworkList, WlanGetNetworkBssList, WlanQueryInterface(current_connection), WlanScan and
/// WlanRegisterNotification with MSM sources), so making a report can never trigger a location prompt.
/// WlanGetProfile (the profile XML, which has the SSID) isn't used: that page doesn't explicitly clear it.
/// A few quick local calls to the WLAN service; every failure is ignored.
///
/// Layouts (wlanapi.h; identical on x86 and x64 – only GUIDs, WCHARs and DWORDs):
///   WLAN_INTERFACE_INFO_LIST: dwNumberOfItems 0, dwIndex 4, InterfaceInfo[] at 8
///   WLAN_INTERFACE_INFO (532 bytes): InterfaceGuid 0, strInterfaceDescription WCHAR[256] 16, isState 528
///   WLAN_PROFILE_INFO_LIST: dwNumberOfItems 0, dwIndex 4, ProfileInfo[] at 8
///   WLAN_PROFILE_INFO (516 bytes): strProfileName WCHAR[256] 0, dwFlags 512
/// </summary>
internal static class WlanNames
{
    private const int ListHeader = 8, InfoSize = 532, ProfileSize = 516, MaxInterfaces = 32;
    internal const int MaxProfiles = 200;

    /// <summary>Saved Wi-Fi profile names across all wireless interfaces (distinct, non-empty, at most 200). Never throws.</summary>
    public static List<string> Current()
    {
        var names = new List<string>();
        IntPtr h = IntPtr.Zero, ifaces = IntPtr.Zero;
        try
        {
            if (WlanOpenHandle(WLAN_CLIENT_VERSION_2, IntPtr.Zero, out _, out h) != NO_ERROR || h == IntPtr.Zero) return names;
            if (WlanEnumInterfaces(h, IntPtr.Zero, out ifaces) != NO_ERROR || ifaces == IntPtr.Zero) return names;
            int n = Marshal.ReadInt32(ifaces, 0);
            if (n < 0 || n > MaxInterfaces) return names;
            for (int i = 0; i < n && names.Count < MaxProfiles; i++)
            {
                var guid = Marshal.PtrToStructure<Guid>(ifaces + ListHeader + i * InfoSize);
                if (WlanGetProfileList(h, guid, IntPtr.Zero, out IntPtr list) != NO_ERROR || list == IntPtr.Zero) continue;
                try { ParseProfileList(list, names); }
                finally { WlanFreeMemory(list); }
            }
        }
        catch { /* wlanapi missing (e.g. Server without the WLAN service), or anything else: report without it */ }
        finally
        {
            if (ifaces != IntPtr.Zero) try { WlanFreeMemory(ifaces); } catch { }
            if (h != IntPtr.Zero) try { WlanCloseHandle(h, IntPtr.Zero); } catch { }
        }
        return names;
    }

    /// <summary>Add the profile names from a WLAN_PROFILE_INFO_LIST to <paramref name="into"/> (distinct, capped).</summary>
    internal static void ParseProfileList(IntPtr list, List<string> into)
    {
        if (list == IntPtr.Zero) return;
        int n = Marshal.ReadInt32(list, 0);
        if (n <= 0) return;
        n = Math.Min(n, MaxProfiles);   // the buffer is sized by Windows for its own count; never read past that count
        for (int i = 0; i < n && into.Count < MaxProfiles; i++)
        {
            string name = Marshal.PtrToStringUni(list + ListHeader + i * ProfileSize, 256);
            int z = name.IndexOf('\0');
            if (z >= 0) name = name[..z];
            if (name.Trim().Length > 0 && !into.Contains(name)) into.Add(name);
        }
    }
}
