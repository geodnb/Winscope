using System.Text.RegularExpressions;

namespace WinScope.Modules.Events;

/// <summary>
/// Shortens Windows device ids for reports. The last segment of an instance id is often a serial number
/// (USB\VID_0781&amp;PID_5581\4C530001234567891234) or a paired device's Bluetooth address (BTHENUM\…&amp;A4C3F0123456_C00000000),
/// and device interface paths (\\?\USB#VID_0781&amp;PID_5581#4C53…#{guid}) carry the same serial between '#'s.
/// Only the bus and the vendor/product ids are kept: "USB\VID_0781&amp;PID_5581", "PCI\VEN_10DE&amp;DEV_2704",
/// "USBSTOR\Ven_SanDisk&amp;Prod_Cruzer", "BTHENUM". Account ids (local S-1-5-21-… and Entra S-1-12-1-…) are replaced too.
/// </summary>
public static class DeviceIds
{
    private const string Buses = "USBSTOR|USB|PCI|HID|BTHENUM|BTHLEDEVICE|BTHLE|BTH|SWD|ACPI|HDAUDIO|INTELAUDIO|SCSI|STORAGE|ROOT|DISPLAY|MONITOR|UMB|WPDBUSENUM|NVME|SD|TBT|BTHHFENUM|MMDEVAPI";
    private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Device interface path: \\?\BUS#…  or  \??\BUS#…  (the whole thing up to whitespace/punctuation, GUID included).
    private static readonly Regex Interface = new(@"\\(?:\\\?|\?\?)\\(?<bus>" + Buses + @")#[^\s,;()""'<>]*", Opt);
    // Device instance id: BUS\…
    private static readonly Regex Instance = new(@"(?<![\w\\])(?<bus>" + Buses + @")\\[^\s,;()""'<>]+", Opt);
    private static readonly Regex VidPid = new(@"VID_[0-9A-F]{4}&PID_[0-9A-F]{4}|VEN_[0-9A-F]{4}&DEV_[0-9A-F]{4}", Opt);
    // USB storage: Ven_<vendor>&Prod_<product> (free text up to the next '&', '#' or '\').
    private static readonly Regex VenProd = new(@"Ven_[^&#\\]+&Prod_[^&#\\]+", Opt);
    // Account security identifiers: local/domain (S-1-5-21-a-b-c[-rid]) and Microsoft Entra ID (S-1-12-1-a-b-c-d).
    private static readonly Regex Sid = new(@"\bS-1-(?:5-21|12-1)(?:-\d+){3,4}\b", RegexOptions.CultureInvariant);

    /// <summary>Replace every device instance id / interface path in <paramref name="text"/> with its bus and vendor/product ids.</summary>
    public static string Scrub(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = Interface.Replace(text, m => Reduce(m.Groups["bus"].Value, m.Value[(m.Groups["bus"].Index - m.Index + m.Groups["bus"].Length)..]));
        text = Instance.Replace(text, m =>
        {
            string id = m.Value;
            int hash = id.IndexOf('#');
            if (hash >= 0) id = id[..hash];                                   // interface path inside: rest repeats the serial
            return Reduce(m.Groups["bus"].Value, id);
        });
        return Sid.Replace(text, "[account id]");
    }

    /// <summary>Bus plus the first vendor/product pair found in <paramref name="rest"/> – nothing else survives.</summary>
    private static string Reduce(string bus, string rest)
    {
        bus = bus.ToUpperInvariant();
        var vp = VidPid.Match(rest);
        if (vp.Success) return bus + "\\" + vp.Value.ToUpperInvariant();
        var ven = VenProd.Match(rest);
        if (ven.Success) return bus + "\\" + ven.Value;
        return bus;
    }
}
