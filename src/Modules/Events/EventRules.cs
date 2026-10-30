using System.Globalization;
using System.Text.RegularExpressions;

namespace WinScope.Modules.Events;

/// <summary>How to read and explain one provider/event id.</summary>
public sealed class Rule
{
    /// <summary>EventData names to fetch with a property selector (more robust than positions across Windows builds).</summary>
    public string[] Names { get; init; } = Array.Empty<string>();
    /// <summary>Whether the builder needs EventRecord.Properties (positional values).</summary>
    public bool NeedsProps { get; init; }
    public required Func<RawEvent, Classified?> Build { get; init; }
}

/// <summary>
/// The curated table: provider + event id → category, severity, plain-English title/explanation and things to try.
/// Positional property indexes follow the providers' message templates (%1 = index 0). Where a provider has
/// named EventData we ask for the name first and fall back to the position. Anything marked "UNVERIFIED" is from
/// memory of the event's XML and should be checked against a real PC (see the report).
/// </summary>
public static class EventRules
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly Dictionary<string, Dictionary<int, Rule>> ById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Rule> AnyId = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<(Regex Match, Rule Rule)> Patterns = new();
    private static readonly Rule OtherRule = new() { Build = Other };

    static EventRules() => Register();

    private static int _systemDisk = -1;
    /// <summary>Disk number holding Windows (IOCTL_STORAGE_GET_DEVICE_NUMBER on C:), or null if unknown. Set by the engine.</summary>
    public static int? SystemDiskNumber
    {
        get { int v = Volatile.Read(ref _systemDisk); return v < 0 ? null : v; }
        set => Volatile.Write(ref _systemDisk, value ?? -1);
    }

    /// <summary>Which rule handles this event, before reading its properties. Null = not interesting (drop it).</summary>
    public static Rule? Find(string provider, int id, byte level)
    {
        if (ById.TryGetValue(provider, out var m) && m.TryGetValue(id, out var r)) return r;
        if (AnyId.TryGetValue(provider, out r)) return r;
        foreach (var (match, rule) in Patterns)
            if (match.IsMatch(provider)) return rule;
        return level is 1 or 2 ? OtherRule : null;
    }

    public static Classified? Classify(RawEvent e)
    {
        var r = Find(e.Provider, e.Id, e.Level);
        if (r == null) return null;
        try
        {
            var c = r.Build(e);
            if (c != null) return c;
        }
        catch (Exception) when (r != OtherRule)
        {
            // An unexpected property shape must never lose an error – fall through to the generic text.
        }
        return r != OtherRule && e.Level is 1 or 2 ? Other(e) : null;
    }

    // =============================================================================================== registration

    private static void Add(string provider, int id, Rule rule)
    {
        if (!ById.TryGetValue(provider, out var m)) ById[provider] = m = new Dictionary<int, Rule>();
        m[id] = rule;
    }

    private static void Add(string provider, int[] ids, Rule rule) { foreach (int id in ids) Add(provider, id, rule); }

    private static Rule R(Func<RawEvent, Classified?> b, params string[] names) => new() { Build = b, NeedsProps = true, Names = names };
    private static Rule S(Func<RawEvent, Classified?> b) => new() { Build = b };

    private static Classified Make(RawEvent e, string rule, EvCategory cat, EvSeverity sev, string title, string explain,
        string? kind, IEnumerable<string>? tips = null) => new()
    {
        Ev = e, Rule = rule, Category = cat, Severity = sev, Title = title, Explain = explain, Kind = kind,
        Tips = tips?.Where(t => !string.IsNullOrEmpty(t)).Distinct().Take(5).ToArray() ?? Array.Empty<string>()
    };

    private static void Register()
    {
        // ------------------------------------------------------------------ crashes & freezes
        // System: <Provider Name="Microsoft-Windows-WER-SystemErrorReporting" EventSourceName="BugCheck"/>, data param1..3.
        Add("Microsoft-Windows-WER-SystemErrorReporting", 1001, R(BlueScreen1001, "param1", "param2"));
        // BugcheckCode is named EventData index 0 (decimal UInt32). SleepInProgress / PowerButtonTimestamp read by name only.
        Add("Microsoft-Windows-Kernel-Power", 41, R(KernelPower41, "BugcheckCode", "SleepInProgress", "PowerButtonTimestamp"));
        Add("EventLog", 6008, R(EventLog6008));
        // Application Error 1000: AppName(0) AppVersion(1) AppTimeStamp(2) ModuleName(3) ModuleVersion(4)
        // ModuleTimeStamp(5) ExceptionCode(6) FaultingOffset(7) ProcessId(8) ProcessCreationTime(9) AppPath(10) ModulePath(11).
        Add("Application Error", 1000, R(AppCrash1000, "AppName", "ModuleName", "ExceptionCode", "AppPath"));
        // Application Hang 1002: AppName(0) AppVersion(1) ProcessID(2) StartTime(3) TerminationTime(4) ExeFileName(5).
        Add("Application Hang", 1002, R(AppHang1002, "AppName"));
        // WER 1001 (Information): Bucket(0) BucketType(1) EventName(2) Response(3) CabId(4) P1..P10(5..14). Unnamed data.
        Add("Windows Error Reporting", 1001, R(Wer1001));
        // .NET Runtime 1026: data[0] is the whole "Application: x.exe\nFramework Version: …\nDescription: …" text.
        Add(".NET Runtime", 1026, R(DotNet1026));
        Add("Microsoft-Windows-Resource-Exhaustion-Detector", 2004, R(LowMemory2004));

        // ------------------------------------------------------------------ graphics & drivers
        Add("Display", 4101, R(Display4101));                       // data[0] = driver name, e.g. "nvlddmkm"
        Add("nvlddmkm", 153, S(e => Nvidia(e, 153)));
        Add("nvlddmkm", 13, S(e => Nvidia(e, 13)));
        Add("nvlddmkm", 14, S(e => Nvidia(e, 14)));
        AnyId["nvlddmkm"] = S(e => Nvidia(e, e.Id));
        Add("NVIDIA OpenGL Driver", 1, S(NvidiaOpenGl));           // UNVERIFIED provider name (Application log)
        foreach (var p in new[] { "amdkmdag", "amdkmdap", "amdwddmg", "amdkmpfd" }) AnyId[p] = S(e => OtherGpu(e, "AMD"));
        Patterns.Add((new Regex("^igfx", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), S(e => OtherGpu(e, "Intel"))));
        Add("Microsoft-Windows-Kernel-PnP", 219, R(PnP219, "DriverName", "FailureName"));  // UNVERIFIED names/positions
        Add("Microsoft-Windows-Kernel-PnP", 411, R(PnP411, "DeviceInstanceId"));             // [0] is DeviceInstanceIdLength, [1] the id
        Add("Microsoft-Windows-Kernel-PnP", 225, S(e => Harmless(e, EvCategory.Hardware, "An app delayed removing a device",
            "A program was still using a device (often a USB drive) when Windows tried to remove or eject it. Nothing is wrong.")));

        // ------------------------------------------------------------------ hardware
        Add("Microsoft-Windows-WHEA-Logger", 17, S(e => Whea(e, false, "PCI Express")));
        Add("Microsoft-Windows-WHEA-Logger", 19, S(e => Whea(e, false, "CPU")));
        Add("Microsoft-Windows-WHEA-Logger", 47, S(e => Whea(e, false, "memory")));
        Add("Microsoft-Windows-WHEA-Logger", 18, S(e => Whea(e, true, "CPU")));
        // 20 / 46: fatal or corrected decided by level (Error/Critical = fatal), like the catch-all – ids UNVERIFIED.
        Add("Microsoft-Windows-WHEA-Logger", 20, S(e => Whea(e, e.Level is 1 or 2, "PCI Express")));
        Add("Microsoft-Windows-WHEA-Logger", 46, S(e => Whea(e, e.Level is 1 or 2, "memory")));
        AnyId["Microsoft-Windows-WHEA-Logger"] = S(e => e.Level is 1 or 2 ? Whea(e, true, null) : e.Level == 3 ? Whea(e, false, null) : null);
        Add("disk", new[] { 7, 11, 51, 153, 154 }, R(Disk));
        foreach (var ntfs in new[] { "Ntfs", "Microsoft-Windows-Ntfs" })
            Add(ntfs, new[] { 55, 98, 50, 57, 140 }, R(Ntfs));
        Add("volmgr", 161, S(e => Make(e, "dumpfail", EvCategory.Hardware, EvSeverity.Problem, "Windows couldn't save a crash dump",
            "After a crash Windows tries to save a 'dump' file describing what went wrong. This time it couldn't, so there's less to go on if it happens again.",
            Kinds.DumpFail, new[]
            {
                "Make sure the page file is on: Settings › System › About › Advanced system settings › Performance › Advanced › Virtual memory (System managed).",
                "Keep a few GB free on C:.",
                "Startup and Recovery (same Advanced tab): set 'Write debugging information' to Automatic memory dump.",
            })));
        Add("volmgr", 46, S(e => Harmless(e, EvCategory.Hardware, "Crash dump setup skipped at start-up",
            "Windows couldn't prepare crash-dump writing early in start-up, usually because the page file wasn't ready yet. It's very common and normally harmless.")));
        foreach (var p in new[] { "storahci", "stornvme", "iaStorAC", "iaStorAVC", "iaStorA", "iaStorV", "iaVROC", "amdsata", "amdsbs", "nvstor", "storport" })
        {
            Add(p, 129, S(StorageReset));
            Add(p, 11, S(StorageControllerError));
        }

        // ------------------------------------------------------------------ network
        // WLAN-AutoConfig EventData names: InterfaceGuid(0) InterfaceDescription(1) ConnectionMode(2) ProfileName(3)
        // SSID(4) BSSType(5) Reason / FailureReason(6) … – positions UNVERIFIED, names used first.
        Add("Microsoft-Windows-WLAN-AutoConfig", 8003, R(Wlan8003, "SSID", "Reason", "InterfaceDescription", "ProfileName"));
        Add("Microsoft-Windows-WLAN-AutoConfig", 8002, R(Wlan8002, "SSID", "FailureReason", "InterfaceDescription", "ProfileName"));
        Add("Microsoft-Windows-WLAN-AutoConfig", 11006, R(Wlan11006, "SSID"));              // UNVERIFIED
        Patterns.Add((new Regex(@"^Netw[a-z]w\d+$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), S(IntelWifi)));
        Patterns.Add((new Regex(@"^(e1[a-z]\w*|e2f\w*|ixgbe\w*|ixn\w*|i40e\w*|rt640x64|rt68cx21\w*|rtcx21\w*|rt25cx21\w*|rtux64w10|rtots64|b57nd60a|k57nd60a|bxnd\w*)$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), S(Ethernet)));
        Add("Microsoft-Windows-NDIS", 10400, S(e => Make(e, "ndisreset", EvCategory.Network, EvSeverity.Problem, "Network adapter was reset",
            "A network adapter stopped responding and Windows reset it. The connection drops for a few seconds while this happens.", Kinds.NetAdapter, new[]
            {
                "Update the network adapter's driver from its maker (Intel, Realtek etc.) rather than relying on Windows Update.",
                "Device Manager › Network adapters › the adapter › Power Management: untick 'Allow the computer to turn off this device to save power'.",
            }) with { ShowMessageLine = true }));

        // ------------------------------------------------------------------ updates
        // WindowsUpdateClient: 20 = errorCode(0) updateTitle(1) updateGuid(2) …; 19 / 43 = updateTitle(0) updateGuid(1) …
        Add("Microsoft-Windows-WindowsUpdateClient", 20, R(Update20, "errorCode", "updateTitle"));
        Add("Microsoft-Windows-WindowsUpdateClient", 19, R(e => UpdateInfo(e, true), "updateTitle"));
        Add("Microsoft-Windows-WindowsUpdateClient", 43, R(e => UpdateInfo(e, false), "updateTitle"));

        // ------------------------------------------------------------------ power & startup (information level)
        Add("Microsoft-Windows-Kernel-General", 12, S(e => Make(e, "kg12", EvCategory.Power, EvSeverity.Info, "Windows started",
            "The PC started up.", null)));
        // Kernel-Boot 27 "The boot type was 0x%1": 0 = full start, 1 = Fast Startup (hybrid boot), 2 = resume from hibernate.
        Add("Microsoft-Windows-Kernel-Boot", 27, R(KernelBoot27, "BootType"));               // UNVERIFIED name/position 0
        Add("Microsoft-Windows-Kernel-General", 13, S(e => Make(e, "kg13", EvCategory.Power, EvSeverity.Info, "Windows shut down",
            "Windows shut down normally.", null)));
        Add("Microsoft-Windows-Kernel-Power", 42, S(e => Make(e, "kp42", EvCategory.Power, EvSeverity.Info, "PC went to sleep",
            "The PC went into sleep (or hibernate).", null)));
        Add("Microsoft-Windows-Kernel-Power", 107, S(e => Make(e, "kp107", EvCategory.Power, EvSeverity.Info, "PC woke from sleep",
            "The PC resumed from sleep.", null)));
        Add("Microsoft-Windows-Kernel-Power", 137, S(e => Harmless(e, EvCategory.Power, "Firmware power note after waking",
            "The motherboard firmware adjusted some low-level settings across a sleep or hibernate. It's informational and harmless.")));
        Add("Microsoft-Windows-Power-Troubleshooter", 1, R(Wake1, "WakeSourceText"));          // UNVERIFIED name
        // User32 1074: param1 process (0), computer (1), reason (2), reason code (3), shutdown type (4), comment (5), user (6).
        Add("User32", 1074, R(User32_1074));
        Add("Microsoft-Windows-Kernel-Processor-Power", 37, S(e => Harmless(e, EvCategory.Power, "CPU speed limited by firmware",
            "The motherboard firmware briefly limited the processor's speed – very common at start-up while power management settles. Only worth a look if it keeps appearing while you're gaming.")));

        // ------------------------------------------------------------------ services
        // Service Control Manager: classic param1..paramN; message %1 = index 0.
        Add("Service Control Manager", new[] { 7031, 7034 }, R(ServiceCrash));
        Add("Service Control Manager", new[] { 7000, 7009, 7023, 7001 }, R(ServiceFail));

        // ------------------------------------------------------------------ known harmless noise
        Add("Microsoft-Windows-DistributedCOM", 10016, S(e => Harmless(e, EvCategory.Other, "DCOM permission warning",
            "A Windows component asked for a permission it isn't set up to have. Microsoft documents this DCOM 10016 warning as expected and safe to ignore.")));
        Add("Microsoft-Windows-DistributedCOM", 10010, S(e => Harmless(e, EvCategory.Other, "A background component started slowly",
            "A Windows background component took too long to start and register. It's usually a one-off at start-up and harmless – unless an app failed to open at the same moment, in which case that app's component is the likely cause.")));
        AnyId["Microsoft-Windows-Kernel-EventTracing"] = S(e => Harmless(e, EvCategory.Other, "Diagnostic logging session hiccup",
            "One of Windows' own diagnostic logging sessions couldn't start or ran out of buffer space. It only affects Windows' internal diagnostics, not your PC."));
        foreach (var p in new[] { "Microsoft-Windows-Perflib", "Perflib" })
            AnyId[p] = S(e => Harmless(e, EvCategory.Other, "Performance counter failed to load",
                "A performance counter (used by monitoring tools) couldn't load. Harmless unless a monitoring tool you rely on is missing data."));
        AnyId["Microsoft-Windows-Time-Service"] = S(e => Harmless(e, EvCategory.Other, "Clock sync with the time server failed",
            "Windows couldn't sync the clock with the internet time server this time. It retries by itself and a single failure doesn't matter – if your clock is visibly wrong, use Settings › Time & language › Date & time › Sync now."));
        AnyId["Schannel"] = S(e => Harmless(e, EvCategory.Network, "Secure connection declined",
            "A secure (TLS) connection between an app and a website was declined by one side. This is everyday background noise."));
        Add("ESENT", new[] { 455, 489, 490, 642, 916 }, S(e => Harmless(e, EvCategory.Other, "Windows database hiccup",
            "A small Windows database (used by things like Search and the Start menu) logged a hiccup. It sorts itself out.")));
        Add("Microsoft-Windows-Security-SPP", 16385, S(e => Harmless(e, EvCategory.Other, "Licence service couldn't schedule a check",
            "Windows' licensing service couldn't schedule its next check. It tries again later; activation isn't affected.")));
        Add("Microsoft-Windows-WMI", 10, S(e => Harmless(e, EvCategory.Other, "Old management filter failed to register",
            "A leftover Windows management (WMI) filter failed to register at start-up. Harmless.")));
        Add("VSS", new[] { 8193, 8194, 13 }, S(e => Harmless(e, EvCategory.Other, "Shadow copy service error",
            "The Volume Shadow Copy service (used by System Restore and backup tools) logged an error. Only matters if your backups or restore points are failing.")));
        Add("Microsoft-Windows-HttpEvent", 15016, S(e => Harmless(e, EvCategory.Other, "Web service security package not available",
            "A built-in Windows web component couldn't load an enterprise sign-in package. Harmless on home PCs.")));
        Add("Microsoft-Windows-DNS-Client", 1014, S(e => Harmless(e, EvCategory.Network, "A name lookup timed out",
            "Looking up a web address timed out once – often a background check such as 'wpad'. Only worth a look if websites actually fail to load.")));
        Add("BTHUSB", 17, S(e => Harmless(e, EvCategory.Hardware, "Bluetooth adapter lacks an optional feature",
            "Your Bluetooth adapter doesn't support an optional Bluetooth Low Energy feature. Harmless.")));
        Add("Microsoft-Windows-Immersive-Shell", 5973, S(e => Harmless(e, EvCategory.Other, "A Store app failed to start in the background",
            "A Microsoft Store app failed to start in the background. Harmless unless that app won't open for you.")));
    }

    /// <summary>
    /// Providers suppressed from the main query and read by a separate, capped "noise" query instead, so that
    /// chatty harmless warnings can't push real events past the per-log read limit. Ids null = all ids.
    /// </summary>
    public static readonly (string Provider, int[]? Ids)[] NoiseSources =
    {
        ("Microsoft-Windows-DistributedCOM", new[] { 10016, 10010 }),
        ("Schannel", null),
        ("Microsoft-Windows-Time-Service", null),
        ("Microsoft-Windows-DNS-Client", new[] { 1014 }),
        ("Microsoft-Windows-Kernel-EventTracing", null),
        ("Microsoft-Windows-Perflib", null),
        ("Perflib", null),
        ("ESENT", new[] { 455, 489, 490, 642, 916 }),
    };

    // =============================================================================================== shared tips

    private const string TipNvidiaDriver = "Update the NVIDIA driver – or for a clean start, remove it with DDU (Display Driver Uninstaller) in Safe Mode and install the latest one fresh.";
    private const string TipGpuStock = "Remove any GPU overclock or undervolt (MSI Afterburner, NVIDIA app tuning) and test at stock settings.";
    private const string TipGpuPower = "Check the graphics card's power cable is pushed fully home – on an RTX 4080 the 12VHPWR / 12V-2x6 plug must sit flush with no gap.";
    private const string TipTemps = "Check temperatures under load (WinScope's Performance tool or HWiNFO) – a very hot GPU or CPU can cause this.";
    private const string TipCpuStock = "Turn off CPU and memory overclocks, including XMP/EXPO memory profiles, and see if it still happens.";
    private const string TipBios = "Update the motherboard BIOS – updates often include stability fixes (Intel 13th/14th-gen CPUs especially need the latest microcode).";
    private const string TipMemTest = "Test the memory: run Windows Memory Diagnostic (type mdsched in Start) or MemTest86 overnight.";
    private const string TipDrivers = "Update chipset, network and graphics drivers from the PC or motherboard maker's website.";
    private const string TipRecent = "If this started after installing something new (a driver, anti-cheat, RGB or fan software, a VPN), try removing it.";
    private const string TipDump = "A free tool like WhoCrashed (or Microsoft's WinDbg) can read the crash dump and name the driver involved.";
    private const string TipSfc = "In a terminal opened as administrator run: sfc /scannow – then DISM /Online /Cleanup-Image /RestoreHealth.";
    private const string TipDiskHealth = "Check the drive's health with CrystalDiskInfo or the maker's tool (Samsung Magician, WD Dashboard and so on).";
    private const string TipBackup = "Back up anything important now, just in case.";
    private const string TipCables = "Check the drive's cables (or reseat the M.2 SSD) and try a different SATA port or cable.";
    private const string TipSsdFw = "Update the SSD's firmware with the maker's tool.";
    private const string TipLspm = "Try turning off PCI Express power saving: Control Panel › Power Options › Change plan settings › Change advanced power settings › PCI Express › Link State Power Management › Off.";
    private const string TipWifiDriver = "Install the latest Intel Wi-Fi driver from Intel (Intel Driver & Support Assistant) – it's usually newer than the one Windows Update provides.";
    private const string TipWifiPower = "Device Manager › Network adapters › your Wi-Fi adapter › Power Management: untick 'Allow the computer to turn off this device to save power'.";
    private const string TipWifiPlan = "Power Options › Change advanced power settings › Wireless Adapter Settings › Power Saving Mode: Maximum Performance.";
    private const string TipWifiBt = "If drops happen while using Bluetooth (headset or controller), the AX210 shares its antennas between the two – use 5 GHz or 6 GHz Wi-Fi rather than 2.4 GHz.";
    private const string TipRouter = "Check whether the router restarted or changed channel at that time – AirScope or the router's own log will show it.";
    private const string TipGameFiles = "For games: verify (repair) the game files in Steam, Epic, the EA app or Battle.net.";
    private const string TipVcRedist = "If it keeps happening, reinstall the latest Microsoft Visual C++ Redistributable (x64).";
    private const string TipUpdateApp = "Update the program or game to the latest version.";

    // =============================================================================================== blue screens

    private static readonly Dictionary<ulong, (string Name, string Family)> StopCodes = new()
    {
        [0x0A] = ("IRQL_NOT_LESS_OR_EQUAL", "driver"),
        [0x1E] = ("KMODE_EXCEPTION_NOT_HANDLED", "driver"),
        [0x19] = ("BAD_POOL_HEADER", "memory"),
        [0x1A] = ("MEMORY_MANAGEMENT", "memory"),
        [0x24] = ("NTFS_FILE_SYSTEM", "storage"),
        [0x3B] = ("SYSTEM_SERVICE_EXCEPTION", "driver"),
        [0x3D] = ("INTERRUPT_EXCEPTION_NOT_HANDLED", "driver"),
        [0x4E] = ("PFN_LIST_CORRUPT", "memory"),
        [0x50] = ("PAGE_FAULT_IN_NONPAGED_AREA", "memory"),
        [0x51] = ("REGISTRY_ERROR", "storage"),
        [0x7A] = ("KERNEL_DATA_INPAGE_ERROR", "storage"),
        [0x7B] = ("INACCESSIBLE_BOOT_DEVICE", "storage"),
        [0x7E] = ("SYSTEM_THREAD_EXCEPTION_NOT_HANDLED", "driver"),
        [0x7F] = ("UNEXPECTED_KERNEL_MODE_TRAP", "hardware"),
        [0x8E] = ("KERNEL_MODE_EXCEPTION_NOT_HANDLED", "driver"),
        [0x9C] = ("MACHINE_CHECK_EXCEPTION", "hardware"),
        [0x9F] = ("DRIVER_POWER_STATE_FAILURE", "power"),
        [0xA0] = ("INTERNAL_POWER_ERROR", "power"),
        [0xBE] = ("ATTEMPTED_WRITE_TO_READONLY_MEMORY", "driver"),
        [0xC2] = ("BAD_POOL_CALLER", "driver"),
        [0xC4] = ("DRIVER_VERIFIER_DETECTED_VIOLATION", "driver"),
        [0xC5] = ("DRIVER_CORRUPTED_EXPOOL", "driver"),
        [0xD1] = ("DRIVER_IRQL_NOT_LESS_OR_EQUAL", "driver"),
        [0xE2] = ("MANUALLY_INITIATED_CRASH", "manual"),
        [0xEA] = ("THREAD_STUCK_IN_DEVICE_DRIVER", "graphics"),
        [0xED] = ("UNMOUNTABLE_BOOT_VOLUME", "storage"),
        [0xEF] = ("CRITICAL_PROCESS_DIED", "system"),
        [0xF4] = ("CRITICAL_OBJECT_TERMINATION", "storage"),
        [0xFC] = ("ATTEMPTED_EXECUTE_OF_NOEXECUTE_MEMORY", "driver"),
        [0x101] = ("CLOCK_WATCHDOG_TIMEOUT", "hardware"),
        [0x109] = ("CRITICAL_STRUCTURE_CORRUPTION", "memory"),
        [0x10E] = ("VIDEO_MEMORY_MANAGEMENT_INTERNAL", "graphics"),
        [0x113] = ("VIDEO_DXGKRNL_FATAL_ERROR", "graphics"),
        [0x116] = ("VIDEO_TDR_FAILURE", "graphics"),
        [0x117] = ("VIDEO_TDR_TIMEOUT_DETECTED", "graphics"),
        [0x119] = ("VIDEO_SCHEDULER_INTERNAL_ERROR", "graphics"),
        [0x124] = ("WHEA_UNCORRECTABLE_ERROR", "hardware"),
        [0x12B] = ("FAULTY_HARDWARE_CORRUPTED_PAGE", "memory"),
        [0x133] = ("DPC_WATCHDOG_VIOLATION", "driver"),
        [0x139] = ("KERNEL_SECURITY_CHECK_FAILURE", "driver"),
        [0x13A] = ("KERNEL_MODE_HEAP_CORRUPTION", "driver"),
        [0x144] = ("BUGCODE_USB3_DRIVER", "driver"),
        [0x154] = ("UNEXPECTED_STORE_EXCEPTION", "storage"),
        [0x1C7] = ("STORE_DATA_STRUCTURE_CORRUPTION", "storage"),
        [0xC000021A] = ("STATUS_SYSTEM_PROCESS_TERMINATED", "system"),
    };

    /// <summary>0x1000007E etc. are the same bugchecks reported "with parameters": normalise to 0x7E.</summary>
    public static ulong NormStop(ulong code) => code >= 0x10000000 && code < 0x20000000 ? code & 0x0FFFFFFF : code;

    public static string StopHex(ulong code) => "0x" + code.ToString("X", Inv);

    /// <summary>Title, explanation and tips for a stop code (0 = unknown).</summary>
    public static (string Title, string Explain, string[] Tips) BlueScreenText(ulong code)
    {
        ulong key = NormStop(code);
        bool known = StopCodes.TryGetValue(key, out var sc);
        string title = code == 0 ? "Blue screen crash"
            : known ? $"Blue screen: {sc.Name} ({StopHex(code)})"
            : $"Blue screen crash (stop code {StopHex(code)})";
        string family = known ? sc.Family : "unknown";
        return family switch
        {
            "graphics" => (title, "This stop code points at the graphics card or its driver: the GPU stopped responding and Windows couldn't reset it.",
                new[] { TipNvidiaDriver, TipGpuStock, TipGpuPower, TipTemps }),
            "hardware" => (title, "This stop code means the hardware itself – usually the CPU, sometimes the motherboard or power supply – reported an error it couldn't fix.",
                new[] { TipCpuStock, TipBios, TipTemps, "If it still happens at stock settings, the CPU or motherboard may be faulty – consider a warranty claim." }),
            "memory" => (title, "This stop code usually points at memory (RAM): unstable memory settings, a failing stick, or sometimes a driver writing where it shouldn't.",
                new[] { TipMemTest, "Try with XMP/EXPO turned off in the BIOS.", "Reseat the RAM sticks (switch off at the wall first).", TipDump }),
            "driver" => (title, "This stop code usually means a driver (the software that runs a piece of hardware) did something it shouldn't.",
                new[] { TipDump, TipDrivers, TipRecent, TipCpuStock }),
            "storage" => (title, "This stop code points at storage: Windows couldn't read something it needed from the drive.",
                new[] { TipDiskHealth, "Run chkdsk C: /scan in a terminal opened as administrator.", TipSsdFw, TipCables }),
            "power" => (title, "This stop code means a driver didn't respond properly while the PC was going to sleep, waking up or changing power state.",
                new[] { "Update the Wi-Fi, network, chipset and graphics drivers.", "Turn off Fast Startup: Control Panel › Power Options › Choose what the power buttons do.", TipDump }),
            "system" => (title, "A core Windows process stopped unexpectedly, so Windows had to stop too.",
                new[] { TipSfc, TipDiskHealth, TipRecent }),
            "manual" => (title, "This crash was triggered on purpose (for example a debugging keyboard shortcut), not by a fault.", Array.Empty<string>()),
            _ => (title, code == 0
                    ? "Windows didn't record which stop code it was."
                    : "WinScope doesn't have notes for this stop code – searching the web for it usually finds good advice.",
                new[] { TipDump, TipDrivers, TipCpuStock }),
        };
    }

    /// <summary>"0x00000116 (0xffff…, …)" → 0x116.</summary>
    public static ulong ParseBugcheck(string? s)
    {
        if (string.IsNullOrEmpty(s)) return 0;
        var m = Regex.Match(s, @"^\s*(?:0x)?([0-9a-fA-F]{1,16})\b");
        return m.Success && ulong.TryParse(m.Groups[1].Value, NumberStyles.HexNumber, Inv, out var v) ? v : 0;
    }

    private static Classified BlueScreen1001(RawEvent e)
    {
        ulong code = ParseBugcheck(e.Text("param1", 0));
        string? dump = e.Text("param2", 1);
        var (title, explain, tips) = BlueScreenText(code);
        string text = "Windows hit a problem it couldn't recover from and restarted (a blue screen crash). " + explain +
                      (dump != null && dump.Contains('\\') ? $" A crash dump was saved: {dump}." : "");
        return Make(e, "bsod", EvCategory.Crashes, EvSeverity.Critical, title, text, Kinds.Bsod, tips) with { Code = code };
    }

    private static readonly string[] UnexpectedTips =
    {
        "If the screen froze or went black first, it's often the graphics card or its driver – check temperatures and remove overclocks.",
        "Sudden power-offs under gaming load often point to the power supply: check its cables, and that it's big enough (NVIDIA recommends at least 750 W for an RTX 4080).",
        "If you held the power button because the PC froze, note what you were doing – a pattern helps narrow it down.",
    };

    private static Classified KernelPower41(RawEvent e)
    {
        ulong bug = Val.UInt(e.Field("BugcheckCode", 0)) ?? 0;
        if (bug != 0)
        {
            var (title, explain, tips) = BlueScreenText(bug);
            return Make(e, "kp41", EvCategory.Crashes, EvSeverity.Critical, title,
                "Windows restarted after a blue screen crash. " + explain, Kinds.Bsod, tips) with { Code = bug };
        }
        // Read by name only: a wrong positional guess here would give a misleading sentence.
        ulong button = Val.UInt(e.Field("PowerButtonTimestamp")) ?? 0;
        ulong sleep = Val.UInt(e.Field("SleepInProgress")) ?? 0;
        string extra = button != 0 ? " This time the power button was held down to switch it off."
            : sleep != 0 ? " It happened while the PC was going to sleep or waking up." : "";
        return Make(e, "kp41", EvCategory.Crashes, EvSeverity.Critical, "PC turned off without shutting down properly",
            "Windows didn't get to shut down cleanly: the power was cut, the PC froze completely, or the power button was held down. " +
            "Windows can't record more detail than this when it happens." + extra, Kinds.Unexpected, UnexpectedTips);
    }

    private static Classified EventLog6008(RawEvent e)
    {
        string? time = e.TextAt(0), date = e.TextAt(1);
        string title = time != null && date != null ? $"The shutdown at {time} on {date} was unexpected" : "The previous shutdown was unexpected";
        return Make(e, "6008", EvCategory.Crashes, EvSeverity.Critical, title,
            "When Windows started, it found it hadn't been shut down properly last time – the power was cut, the PC froze, or it crashed.",
            Kinds.Unexpected, UnexpectedTips);
    }

    // =============================================================================================== app crashes

    private static readonly Dictionary<ulong, string> ExceptionCodes = new()
    {
        [0xC0000005] = "access violation – it tried to use memory it wasn't allowed to",
        [0xC0000409] = "it detected serious internal damage and stopped itself on purpose (stack buffer overrun / fail-fast)",
        [0xC0000374] = "its memory (heap) got corrupted",
        [0xC00000FD] = "stack overflow",
        [0x80000003] = "it stopped itself after an internal check failed (breakpoint)",
        [0xE0434352] = "an unhandled .NET error inside the program",
        [0xE06D7363] = "an unhandled C++ error inside the program",
        [0xC000001D] = "illegal instruction – often unstable CPU settings, or a CPU feature the program expected",
        [0xC0000006] = "Windows couldn't read part of the program from the drive (in-page error)",
        [0xC0000142] = "a DLL it needed failed to start",
        [0xC0000135] = "a DLL it needed is missing",
        [0xC06D007E] = "a module it needed couldn't be found",
        [0xC0000417] = "it passed an invalid value to a C runtime function",
        [0x40000015] = "it asked to be closed after a fatal error",
        [0xC0000094] = "integer divide by zero",
        [0xC0000096] = "privileged instruction",
        [0xC0000602] = "it stopped itself after detecting a problem (fail-fast)",
        [0xC000041D] = "an unhandled error in a callback",
        [0xC0000025] = "a non-continuable error",
        [0xC0000420] = "an internal assertion failed",
    };

    private static readonly Regex NvModules = new(@"^(nvwgf2um|nvoglv|nvldumd|nvd3dum|nvcuda|nvgpucomp|nvapi|nvlddmkm|nvumdshim|nvwgf2umx|nvoglv64|nvldumdx|nvd3dumx)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    /// <summary>Who a faulting module belongs to, and what to try.</summary>
    public static (string? Who, string[] Tips) ModuleHint(string? module, string app)
    {
        if (string.IsNullOrEmpty(module) || module.Equals("unknown", StringComparison.OrdinalIgnoreCase))
            return ("the program's own code", new[] { TipUpdateApp, TipGameFiles, TipVcRedist });
        string m = module.ToLowerInvariant();
        string a = app.ToLowerInvariant();
        if (m == a || Path.GetFileNameWithoutExtension(m) == Path.GetFileNameWithoutExtension(a))
            return ("the program's own code", new[] { TipUpdateApp, TipGameFiles, TipVcRedist });
        if (NvModules.IsMatch(m))
            return ("the NVIDIA graphics driver", new[] { TipNvidiaDriver, TipGpuStock, "Check for a game update – new games often hit driver bugs that get fixed quickly." });
        if (m.StartsWith("amdxx", StringComparison.Ordinal) || m.StartsWith("atidxx", StringComparison.Ordinal) || m.StartsWith("amdvlk", StringComparison.Ordinal) || m.StartsWith("atiumd", StringComparison.Ordinal))
            return ("the AMD graphics driver", new[] { "Update the AMD graphics driver (AMD Software: Adrenalin).", TipUpdateApp });
        if (m.StartsWith("igd", StringComparison.Ordinal) || m.StartsWith("igxel", StringComparison.Ordinal) || m.StartsWith("igc64", StringComparison.Ordinal))
            return ("the Intel graphics driver", new[] { "Update the Intel graphics driver – or make sure the game runs on the NVIDIA card (Settings › System › Display › Graphics).", TipUpdateApp });
        if (m is "d3d11.dll" or "d3d12.dll" or "d3d12core.dll" or "dxgi.dll" or "d3d9.dll" or "vulkan-1.dll" or "dxcore.dll")
            return ("DirectX / the graphics stack", new[] { TipNvidiaDriver, TipGpuStock, TipUpdateApp });
        if (m.StartsWith("gameoverlayrenderer", StringComparison.Ordinal))
            return ("the Steam overlay", new[] { "Turn off the Steam overlay for this game (Steam › right-click the game › Properties › General).", TipUpdateApp });
        if (m.StartsWith("discordhook", StringComparison.Ordinal))
            return ("Discord's in-game overlay", new[] { "Turn off Discord's in-game overlay (Discord › Settings › Game Overlay).", TipUpdateApp });
        if (m.StartsWith("rtsshooks", StringComparison.Ordinal))
            return ("the RivaTuner / MSI Afterburner overlay", new[] { "Close RivaTuner Statistics Server (or add a profile for this game with detection off).", TipUpdateApp });
        if (m.StartsWith("nvspcap", StringComparison.Ordinal) || m.StartsWith("nvcamera", StringComparison.Ordinal))
            return ("the NVIDIA app overlay", new[] { "Turn off the NVIDIA app overlay (Alt+Z) for a test.", TipNvidiaDriver });
        if (m.StartsWith("igo", StringComparison.Ordinal) && m.EndsWith(".dll", StringComparison.Ordinal))
            return ("the EA app overlay", new[] { "Turn off the EA app in-game overlay.", TipUpdateApp });
        if (m.StartsWith("eosovh", StringComparison.Ordinal))
            return ("the Epic Games overlay", new[] { "Turn off the Epic overlay, or update the Epic Games launcher.", TipUpdateApp });
        if (m is "ntdll.dll" or "kernelbase.dll" or "kernel32.dll" or "ucrtbase.dll" or "msvcp140.dll" or "vcruntime140.dll" or "vcruntime140_1.dll" or "combase.dll" or "user32.dll" or "win32u.dll")
            return ($"a Windows system library ({module}) – the real fault is usually in the program itself", new[] { TipUpdateApp, TipGameFiles, TipVcRedist });
        if (m is "coreclr.dll" or "clr.dll" or "clrjit.dll" || m.StartsWith("system.private.corelib", StringComparison.Ordinal))
            return ("the .NET runtime", new[] { TipUpdateApp, "Install the latest .NET Desktop Runtime from Microsoft." });
        return (module, new[] { TipUpdateApp, TipGameFiles });
    }

    private static string AppDisplay(string? app) => string.IsNullOrEmpty(app) ? "A program" : app;

    private static Classified AppCrash1000(RawEvent e)
    {
        string app = AppDisplay(Val.Name(e.Text("AppName", 0)));
        string? module = Val.Name(e.Text("ModuleName", 3));
        ulong? exc = Val.Hex(e.Field("ExceptionCode", 6));
        var (who, tips) = ModuleHint(module, app);
        bool own = module == null || who == "the program's own code";
        string title = own ? $"{app} crashed" : $"{app} crashed in {module}";
        string text = $"{app} closed suddenly.";
        if (who != null) text += $" The crash happened in {who}.";
        if (exc is ulong x && x != 0)
            text += ExceptionCodes.TryGetValue(x, out var meaning) ? $" Error 0x{x:X8}: {meaning}." : $" Error code 0x{x:X8}.";
        // The overclock/XMP tip is added by the aggregator only when crashes are widespread or there's hardware evidence.
        var allTips = tips.ToList();
        if (exc == 0xC0000006) allTips.Insert(0, TipDiskHealth);
        return Make(e, "app-crash", EvCategory.Crashes, EvSeverity.Problem, title, text, Kinds.AppCrash, allTips) with { Subject = app, Code = exc ?? 0 };
    }

    private static Classified AppHang1002(RawEvent e)
    {
        string app = AppDisplay(Val.Name(e.Text("AppName", 0)));
        return Make(e, "app-hang", EvCategory.Crashes, EvSeverity.Problem, $"{app} stopped responding and was closed",
            $"{app} froze and was closed – by you, or by Windows after you chose to close it.", Kinds.AppHang, new[]
            {
                "Give it a little longer next time – heavy loading can look like a freeze.",
                TipUpdateApp,
                "Check WinScope's Performance tool for memory or the drive being maxed out at that time.",
            }) with { Subject = app };
    }

    private static readonly Dictionary<ulong, (string Title, EvCategory Cat, EvSeverity Sev, string? Kind, string Explain)> LiveKernel = new()
    {
        [0x117] = ("Graphics card stopped responding and was reset", EvCategory.Graphics, EvSeverity.Problem, Kinds.Tdr,
            "Windows noticed the graphics card had hung, reset it and saved a diagnostic snapshot. The screen may have gone black for a moment or a game may have crashed."),
        [0x141] = ("Graphics card stopped responding and was reset", EvCategory.Graphics, EvSeverity.Problem, Kinds.Tdr,
            "Windows noticed the graphics card had hung, reset it and saved a diagnostic snapshot. The screen may have gone black for a moment or a game may have crashed."),
        [0x1B0] = ("Graphics driver failed to start properly", EvCategory.Graphics, EvSeverity.Problem, Kinds.GpuError,   // UNVERIFIED code
            "The graphics driver failed while starting and Windows saved a diagnostic snapshot."),
        [0x193] = ("Graphics driver saved a diagnostic snapshot", EvCategory.Graphics, EvSeverity.Info, null,               // UNVERIFIED code
            "The graphics driver asked Windows to save a diagnostic snapshot. On its own this is usually harmless."),
        [0x144] = ("USB controller problem", EvCategory.Hardware, EvSeverity.Problem, Kinds.Driver,
            "The USB 3 controller's driver hit a problem and Windows saved a diagnostic snapshot. A USB device may have disconnected."),
        [0x15E] = ("Network adapter driver problem", EvCategory.Network, EvSeverity.Problem, Kinds.NetAdapter,             // UNVERIFIED code
            "A network adapter driver stopped responding and Windows saved a diagnostic snapshot."),
        [0x1A1] = ("Windows desktop briefly froze", EvCategory.Other, EvSeverity.Minor, null,                                // UNVERIFIED code
            "Part of the Windows desktop stopped responding for a while and Windows saved a diagnostic snapshot."),
    };

    private static readonly HashSet<string> WerCrashNames = new(StringComparer.OrdinalIgnoreCase)
        { "APPCRASH", "BEX", "BEX64", "MoAppCrash", "CLR20r3", "AppHangB1", "AppHangXProcB1", "MoAppHang", "MoAppHangXProc" };

    /// <summary>WER 1001 Report Id: data[19] – UNVERIFIED position; only used if it parses as a GUID.</summary>
    private static string? WerReportId(RawEvent e) => e.TextAt(19) is string s && Guid.TryParse(s, out var g) ? g.ToString("D") : null;

    private static Classified? Wer1001(RawEvent e) => Wer1001Core(e) is Classified c ? c with { ReportId = WerReportId(e) } : null;

    private static Classified? Wer1001Core(RawEvent e)
    {
        string? name = e.TextAt(2);
        if (name == null) return null;
        if (WerCrashNames.Contains(name))
        {
            // Usually a duplicate of Application Error 1000 / Application Hang 1002 (folded later); kept when it's the only record.
            string? app = Val.Name(e.TextAt(5));
            if (app == null) return null;
            bool hang = name.Contains("Hang", StringComparison.OrdinalIgnoreCase);
            return Make(e, "wer-app", EvCategory.Crashes, EvSeverity.Problem,
                hang ? $"{app} stopped responding" : $"{app} crashed",
                $"Windows Error Reporting recorded {(hang ? "a freeze" : "a crash")} in {app} (report type {name}).",
                hang ? Kinds.AppHang : Kinds.AppCrash, new[] { TipUpdateApp, TipGameFiles }) with { Subject = app };
        }
        if (name.Equals("BlueScreen", StringComparison.OrdinalIgnoreCase))
            return Make(e, "wer-bluescreen", EvCategory.Crashes, EvSeverity.Critical, "Blue screen crash (reported by Windows Error Reporting)",
                "Windows Error Reporting processed a crash dump from a blue screen.", Kinds.Bsod, new[] { TipDump, TipDrivers });
        if (name.Equals("LiveKernelEvent", StringComparison.OrdinalIgnoreCase))
        {
            ulong code = Val.Hex(e.TextAt(5)) ?? 0;                 // P1 = live-dump code in hex, e.g. "141" – UNVERIFIED position
            string hex = StopHex(code);
            if (LiveKernel.TryGetValue(code, out var lk))
            {
                var tips = lk.Cat == EvCategory.Graphics ? new[] { TipNvidiaDriver, TipGpuStock, TipGpuPower, TipTemps }
                    : lk.Cat == EvCategory.Network ? new[]
                    {
                        "Update the network adapter's driver (Wi-Fi or Ethernet) from its maker – Intel, Realtek and so on – rather than relying on Windows Update.",
                        "Device Manager › Network adapters › the adapter › Power Management: untick 'Allow the computer to turn off this device to save power'.",
                    }
                    : new[] { TipDrivers };
                return Make(e, lk.Cat == EvCategory.Graphics ? "wer-lke-gpu" : "wer-lke", lk.Cat, lk.Sev,
                    lk.Title, lk.Explain + $" (LiveKernelEvent {hex})", lk.Kind, lk.Sev >= EvSeverity.Problem ? tips : null) with { Code = code };
            }
            return Make(e, "wer-lke", EvCategory.Other, EvSeverity.Minor, $"Windows saved a diagnostic snapshot (LiveKernelEvent {hex})",
                "A driver hit a problem Windows could recover from, and Windows saved a snapshot for diagnosis. One-offs are usually nothing to worry about.",
                Kinds.Other) with { Code = code };
        }
        return null;   // other report types (Store, update, leak diagnostics…) are routine
    }

    private static Classified? DotNet1026(RawEvent e)
    {
        string? text = e.TextAt(0);
        if (text == null) return null;
        var app = Regex.Match(text, @"Application:\s*(?<a>[^\r\n]+)");
        var ex = Regex.Match(text, @"Exception Info:\s*(?<x>[^\r\n:]+)");
        string name = app.Success ? app.Groups["a"].Value.Trim() : "A .NET program";
        return Make(e, "dotnet-crash", EvCategory.Crashes, EvSeverity.Problem, $"{name} crashed (.NET error)",
            $"{name} stopped because of an error it didn't handle" + (ex.Success ? $" ({ex.Groups["x"].Value.Trim()})." : "."),
            Kinds.AppCrash, new[] { TipUpdateApp, "Install the latest .NET Desktop Runtime from Microsoft." }) with { Subject = name };
    }

    private static Classified LowMemory2004(RawEvent e)
    {
        // The top processes are in UserData; their exact layout varies, so pick out the .exe names in order.
        var names = new List<string>();
        foreach (var p in e.Props)
            if (Val.Str(p) is string raw && raw.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && raw.Length < 260
                && Path.GetFileName(raw.Replace('/', '\\').Split('\\').Last()) is { Length: > 4 } s        // file name only, never a path
                && !names.Contains(s, StringComparer.OrdinalIgnoreCase))
            {
                names.Add(s);
                if (names.Count == 3) break;
            }
        string who = names.Count > 0 ? " The biggest users were: " + string.Join(", ", names) + "." : "";
        return Make(e, "lowmem", EvCategory.Crashes, EvSeverity.Problem,
            names.Count > 0 ? $"Windows was running low on memory ({names[0]} was using the most)" : "Windows was running low on memory",
            "Programs were close to using all of the memory Windows can hand out (RAM plus the page file). Things may have frozen or closed." + who,
            Kinds.LowMemory, new[]
            {
                "Close programs you're not using – browsers with lots of tabs use a surprising amount.",
                "Keep the page file on and 'System managed' (Settings › System › About › Advanced system settings › Performance › Advanced).",
                "If a game uses huge amounts, check for mods or a known memory leak in that game's latest patch.",
            }) with { Subject = names.FirstOrDefault() };
    }

    // =============================================================================================== graphics

    private static string GpuVendor(string? driver)
    {
        if (driver == null) return "graphics";
        if (driver.StartsWith("nv", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        if (driver.StartsWith("amd", StringComparison.OrdinalIgnoreCase) || driver.StartsWith("ati", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (driver.StartsWith("igfx", StringComparison.OrdinalIgnoreCase)) return "Intel";
        return "graphics";
    }

    private static string[] GpuTips(string vendor) => vendor switch
    {
        "AMD" => new[] { "Update the AMD graphics driver (AMD Software: Adrenalin) – a clean install with DDU helps if it keeps happening.", TipGpuStock, TipTemps },
        "Intel" => new[] { "Update the Intel graphics driver, and make sure games run on the NVIDIA card (Settings › System › Display › Graphics).", TipTemps },
        _ => new[] { TipNvidiaDriver, TipGpuStock, TipGpuPower, TipTemps, "If it only happens in one game, turn off its overlays and check that game's known issues." },
    };

    private static Classified Display4101(RawEvent e)
    {
        string? drv = Val.Name(e.TextAt(0));
        string vendor = GpuVendor(drv);
        string name = vendor == "graphics" ? "graphics" : vendor + " graphics";
        return Make(e, "tdr", EvCategory.Graphics, EvSeverity.Problem, "Graphics driver stopped responding and recovered",
            $"Your screen probably went black or froze for a few seconds. The {name} driver{(drv != null ? $" ({drv})" : "")} stopped responding, " +
            "so Windows restarted it – this is called a TDR (timeout detection and recovery). A game running at the time often crashes too.",
            Kinds.Tdr, GpuTips(vendor));
    }

    private static Classified Nvidia(RawEvent e, int id)
    {
        var tips = GpuTips("NVIDIA");
        return id switch
        {
            153 => Make(e, "nv-reset", EvCategory.Graphics, EvSeverity.Problem, "NVIDIA graphics card was reset after an error",
                "The NVIDIA driver reported that the graphics card stopped responding and had to be reset. You may have seen the screen go black for a moment, or a game crash.",
                Kinds.Tdr, tips) with { ShowMessageLine = true },
            13 or 14 => Make(e, "nv-error", EvCategory.Graphics, EvSeverity.Problem, "NVIDIA driver reported a graphics error",
                "The graphics card hit an error while running something – usually a game. It's often a game or driver bug, and sometimes an unstable GPU overclock.",
                Kinds.GpuError, tips) with { ShowMessageLine = true },
            _ => Make(e, "nv-other", EvCategory.Graphics, e.Level is 1 or 2 ? EvSeverity.Problem : EvSeverity.Minor,
                $"NVIDIA driver reported {(e.Level is 1 or 2 ? "an error" : "a warning")} (event {id})",
                "The NVIDIA graphics driver logged this. If it lines up with a crash or black screen, updating or clean-installing the driver is the first thing to try.",
                e.Level is 1 or 2 ? Kinds.GpuError : null, e.Level is 1 or 2 ? tips : null) with { ShowMessageLine = true },
        };
    }

    private static Classified NvidiaOpenGl(RawEvent e) =>
        Make(e, "gl-tdr", EvCategory.Graphics, EvSeverity.Problem, "An OpenGL/Vulkan app lost contact with the NVIDIA driver",
            "The NVIDIA driver took too long to respond (a timeout), so the app using it had to close. This normally goes with a graphics driver reset.",
            Kinds.Tdr, GpuTips("NVIDIA")) with { ShowMessageLine = true };

    private static Classified OtherGpu(RawEvent e, string vendor)
    {
        bool err = e.Level is 1 or 2;
        return Make(e, "gpu-other", EvCategory.Graphics, err ? EvSeverity.Problem : EvSeverity.Minor,
            $"{vendor} graphics driver reported {(err ? "an error" : "a warning")} (event {e.Id})",
            $"The {vendor} graphics driver logged this. If it lines up with a crash or black screen, update the driver.",
            err ? Kinds.GpuError : null, err ? GpuTips(vendor) : null) with { ShowMessageLine = true };
    }

    private static Classified PnP219(RawEvent e)
    {
        string? drv = Val.Name(e.Text("DriverName", 1));
        string? dev = Val.Name(e.Text("FailureName", 4));
        string shortName = drv == null ? "a driver" : drv.Replace(@"\Driver\", "", StringComparison.OrdinalIgnoreCase);
        if (shortName.Equals("WudfRd", StringComparison.OrdinalIgnoreCase))
            return Harmless(e, EvCategory.Hardware, "Placeholder driver didn't load (WudfRd)",
                "Windows tried to load a generic placeholder driver (WudfRd) for a device that doesn't need it. Microsoft says this is safe to ignore.");
        return Make(e, "pnp219", EvCategory.Hardware, EvSeverity.Minor, $"A driver didn't load: {shortName}",
            $"Windows tried to load {shortName} for a device{(dev != null ? $" ({dev})" : "")} and it failed, so that device may not work. " +
            "If everything works, it's usually harmless – some devices log this at every start-up.", null, new[]
            {
                "If a device isn't working, open Device Manager and look for a yellow warning sign, then update or reinstall its driver.",
            });
    }

    private static Classified PnP411(RawEvent e)
    {
        string? dev = Val.Name(e.Text("DeviceInstanceId", 1));
        return Make(e, "pnp411", EvCategory.Hardware, EvSeverity.Problem, "A device had a problem starting",
            $"Windows couldn't start a device{(dev != null ? $" ({dev})" : "")}, so it may not be working.", Kinds.Driver, new[]
            {
                "Open Device Manager and look for a yellow warning sign; update or reinstall that device's driver.",
                "Unplug and reconnect the device if it's external.",
            }) with { ShowMessageLine = true };
    }

    // =============================================================================================== hardware

    private static Classified Whea(RawEvent e, bool fatal, string? part)
    {
        string what = part ?? "hardware";
        if (fatal)
            return Make(e, "whea-fatal", EvCategory.Hardware, EvSeverity.Critical, $"Fatal hardware error ({what})",
                $"The {(part == "CPU" ? "processor" : what)} reported an error it couldn't fix, so Windows had to stop – this normally causes a blue screen (stop code 0x124).",
                Kinds.HwFatal, new[] { TipCpuStock, TipBios, TipTemps, "If it still happens at stock settings, the part may be faulty – consider a warranty claim." })
                with { ShowMessageLine = true, MessageHint = "whea" };
        string explain = part switch
        {
            "PCI Express" => "A device on the PCI Express bus – usually the graphics card, an NVMe SSD or the Wi-Fi card – had a signalling hiccup that was fixed automatically. Often caused by PCIe power saving or a riser cable.",
            "CPU" => "The processor hit an error but fixed it itself, so nothing crashed. An occasional one is harmless; lots of them – or ones during games – usually mean the CPU is running at the edge of stability.",
            "memory" => "Your memory returned an error that was corrected automatically. Frequent ones suggest unstable memory settings (XMP/EXPO) or a failing stick.",
            _ => "A piece of hardware reported an error that was corrected automatically. Occasional ones are harmless; many in a row are worth looking into.",
        };
        string[] tips = part switch
        {
            "PCI Express" => new[] { "Update the BIOS and chipset drivers.", TipLspm, "If the graphics card is on a riser cable (vertical mount), check it, or set that slot to PCIe 3.0 in the BIOS as a test." },
            "CPU" => new[] { "If you've overclocked or undervolted, back it off.", TipBios, TipTemps },
            "memory" => new[] { TipMemTest, "Try with XMP/EXPO turned off in the BIOS.", "Reseat the RAM sticks (switch off at the wall first)." },
            _ => new[] { TipBios, TipCpuStock },
        };
        return Make(e, "whea", EvCategory.Hardware, EvSeverity.Problem, $"Corrected hardware error ({what})", explain, Kinds.HwCorrected, tips)
            with { ShowMessageLine = true, MessageHint = "whea" };
    }

    /// <summary>"\Device\Harddisk1\DR1" → "Disk 1" (the number Disk Management shows).</summary>
    public static string DiskName(string? dev)
    {
        if (dev == null) return "a drive";
        var m = Regex.Match(dev, @"Harddisk(\d+)", RegexOptions.IgnoreCase);
        if (m.Success) return "Disk " + m.Groups[1].Value;
        if (Regex.IsMatch(dev, @"^\d{1,3}$")) return "Disk " + dev;
        return dev;
    }

    private static int? DiskNumber(string? dev)
    {
        if (dev == null) return null;
        var m = Regex.Match(dev, @"Harddisk(\d+)", RegexOptions.IgnoreCase);
        return m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, Inv, out int n) ? n : null;
    }

    /// <summary>Disk 153/154: [0] \Device\HarddiskN\DRN, [1] LBA, [2] disk number, [3] PDO name.</summary>
    private static string DiskOf(RawEvent e)
    {
        string d = DiskName(e.TextAt(0));
        if (!d.StartsWith("Disk ", StringComparison.Ordinal) && e.TextAt(2) is string n && Regex.IsMatch(n, @"^\d{1,3}$")) d = "Disk " + n;
        return d;
    }

    private static bool OnSystemDisk(RawEvent e) =>
        SystemDiskNumber is int sys && (DiskNumber(e.TextAt(0)) == sys || (e.TextAt(2) is string n && n == sys.ToString(Inv)));

    private const string RepeatNote = " A one-off is often a USB stick or card reader being removed; if it keeps happening on an internal drive, act on it.";

    private static Classified? Disk(RawEvent e)
    {
        string[] tips = { TipBackup, TipDiskHealth, TipCables };
        bool sys = OnSystemDisk(e);
        switch (e.Id)
        {
            case 7:
            {
                string d = DiskName(e.TextAt(0));
                return Make(e, "disk", EvCategory.Hardware, EvSeverity.Critical, $"Bad block on {d}{(sys ? " (the Windows drive)" : "")}",
                    "Part of the drive couldn't be read. On a hard drive this is a damaged area; on an SSD it can mean the flash is wearing out. Either way, make sure your files are backed up.",
                    Kinds.Disk, new[] { TipBackup, TipDiskHealth, "Run chkdsk with the /r option on that drive from an admin terminal (it can take hours on a hard drive)." }) with { Subject = d };
            }
            case 11:
            {
                string d = DiskName(e.TextAt(0));
                return Make(e, "disk-ctl", EvCategory.Hardware, sys ? EvSeverity.Critical : EvSeverity.Problem, $"{d}: controller error{(sys ? " (the Windows drive)" : "")}",
                    "Windows lost contact with the drive for a moment. Common causes: a loose or faulty SATA/USB cable, a USB stick or card reader pulled out, or a power-saving glitch." + (sys ? "" : RepeatNote),
                    Kinds.Disk, tips) with { Subject = d, RepeatCritical = true };
            }
            case 51:
            {
                string d = DiskName(e.TextAt(0));
                return Make(e, "disk-page", EvCategory.Hardware, sys ? EvSeverity.Critical : EvSeverity.Problem, $"Paging error on {d}{(sys ? " (the Windows drive)" : "")}",
                    "An error happened while Windows was moving data between memory and the drive. Once is often a USB drive being unplugged; repeated ones suggest a drive or cable problem.",
                    Kinds.Disk, tips) with { Subject = d, RepeatCritical = true };
            }
            case 153:
            {
                string d = DiskOf(e);
                return Make(e, "disk-retry", EvCategory.Hardware, EvSeverity.Problem, $"{d} had to retry a read or write",
                    "The drive took too long to answer and Windows tried again. Occasional retries are harmless; many mean the drive, cable or controller is struggling.",
                    Kinds.Disk, new[] { TipDiskHealth, TipSsdFw, TipCables }) with { Subject = d };
            }
            case 154:
            {
                string d = DiskOf(e);
                return Make(e, "disk", EvCategory.Hardware, EvSeverity.Critical, $"{d}: read or write failed (hardware error)",
                    "The drive reported a hardware error and the operation failed.", Kinds.Disk, tips) with { Subject = d };
            }
        }
        return null;
    }

    private static string? DriveFrom(RawEvent e)
    {
        foreach (var p in e.Props)
            if (Val.Str(p) is string s)
            {
                if (Regex.IsMatch(s, @"^[A-Za-z]:\\?$")) return s.TrimEnd('\\');
                var m = Regex.Match(s, @"HarddiskVolume(\d+)", RegexOptions.IgnoreCase);
                if (m.Success) return "volume " + m.Groups[1].Value;
            }
        return null;
    }

    private static Classified? Ntfs(RawEvent e)
    {
        string drive = DriveFrom(e) ?? "a drive";
        switch (e.Id)
        {
            case 55:
                return Make(e, "ntfs", EvCategory.Hardware, EvSeverity.Critical, $"File system damage found on {drive}",
                    "Windows found damage in the file system structure – the index of where files live. It can lead to missing or unreadable files.",
                    Kinds.Disk, new[] { $"Run chkdsk {(drive.Length == 2 ? drive : "<drive letter>")} /f from an admin terminal (it may ask to run at the next restart).", TipBackup, TipDiskHealth });
            case 98:
                if (e.Level is not (1 or 2 or 3)) return null;   // information-level 98 = "volume is healthy"
                return Make(e, "ntfs", EvCategory.Hardware, EvSeverity.Problem, $"{drive} needs a disk check",
                    "Windows found something on this drive that needs checking and repairing.", Kinds.Disk,
                    new[] { $"Run chkdsk {(drive.Length == 2 ? drive : "<drive letter>")} /f from an admin terminal.", TipBackup });
            case 50:
            case 57:
            case 140:                                           // UNVERIFIED ids for "delayed write failed" / "failed to flush"
                return Make(e, "ntfs-flush", EvCategory.Hardware, EvSeverity.Problem, $"Windows couldn't finish writing to {drive}",
                    "Some data couldn't be written to the drive. This is very common when a USB drive is unplugged without 'Eject' first; on an internal drive it's worth checking its health.",
                    Kinds.Disk, new[] { "Always eject USB drives before unplugging them.", TipDiskHealth });
        }
        return null;
    }

    private static Classified StorageReset(RawEvent e) =>
        Make(e, "storreset", EvCategory.Hardware, EvSeverity.Problem, $"A drive stopped responding and was reset ({e.Provider})",
            "The storage controller had to reset its connection because a drive didn't answer in time. You may have noticed the PC freeze for several seconds.",
            Kinds.Disk, new[]
            {
                "Update the SSD firmware and the chipset / storage drivers (Intel RST or AMD chipset).",
                "Check the SATA cable or the M.2 drive's seating.",
                "Power Options › advanced settings: set 'Turn off hard disk after' to Never, and PCI Express › Link State Power Management to Off.",
            });

    private static Classified StorageControllerError(RawEvent e) =>
        Make(e, "stor-ctl", EvCategory.Hardware, EvSeverity.Problem, $"Storage controller error ({e.Provider})",
            "The drive controller reported an error talking to a drive. One-offs can be a hiccup; repeats point at the drive, its cable or its firmware.",
            Kinds.Disk, new[] { TipBackup, TipDiskHealth, TipSsdFw, TipCables }) with { Subject = e.Provider, RepeatCritical = true };

    // =============================================================================================== network

    /// <summary>True when the Wi-Fi disconnect reason describes a normal, deliberate disconnect (English text).</summary>
    public static bool IsNormalWifiReason(string? reason)
    {
        if (reason == null) return false;
        string r = reason.ToLowerInvariant();
        return r.Contains("by the user") || r.Contains("user wants") || r.Contains("new connection") || r.Contains("explicitly")
               || r.Contains("turned off") || r.Contains("radio") || r.Contains("sleep") || r.Contains("power state")
               || r.Contains("switch") || r.Contains("roam");
    }

    private static string Quote(string s) => "‘" + s.TrimEnd('.') + "’";

    private static Classified Wlan8003(RawEvent e)
    {
        // Subject is only ever a real network name (never the placeholder), so a report can't register "your Wi-Fi network" as one.
        string? net = Val.Str(e.Field("SSID", 4)) ?? Val.Str(e.Field("ProfileName", 3));
        string ssid = net ?? "your Wi-Fi network";
        string? reason = Val.Name(e.Text("Reason", 6));
        if (IsNormalWifiReason(reason))
        {
            string normal = $"A normal disconnect{(reason != null ? ": " + Quote(reason) : "")}.";
            return Make(e, "wifi-normal", EvCategory.Network, EvSeverity.Info, $"Wi-Fi disconnected from {ssid}", normal, null)
                with { Subject = net, WithNetworkName = net == null ? null : n => ($"Wi-Fi disconnected from {n}", normal) };
        }
        string hint = "";
        if (reason != null)
        {
            string r = reason.ToLowerInvariant();
            if (r.Contains("driver")) hint = " The Wi-Fi driver itself dropped the connection, which usually points at the driver or adapter rather than the router.";
            else if (r.Contains("reach") || r.Contains("not available") || r.Contains("no longer") || r.Contains("signal") || r.Contains("lost"))
                hint = " The PC lost contact with the router – weak signal, interference, or the router changing something.";
        }
        string tail = (reason != null ? $" Windows gave this reason: {Quote(reason)}." : "") + hint;
        (string, string) Render(string n) => ($"Wi-Fi dropped: {n}", $"Your PC lost its Wi-Fi connection to {n}." + tail);
        var (title, text) = Render(ssid);
        return Make(e, "wifi-drop", EvCategory.Network, EvSeverity.Problem, title, text,
            Kinds.WifiDrop, new[] { TipWifiDriver, TipWifiPower, TipWifiPlan, TipWifiBt, TipRouter })
            with { NetDrop = true, Subject = net, WithNetworkName = net == null ? null : Render };
    }

    private static Classified Wlan8002(RawEvent e)
    {
        string? net = Val.Str(e.Field("SSID", 4)) ?? Val.Str(e.Field("ProfileName", 3));
        string ssid = net ?? "a Wi-Fi network";
        string? reason = Val.Name(e.Text("FailureReason", 6));
        var tips = new List<string>();
        string r = reason?.ToLowerInvariant() ?? "";
        if (r.Contains("password") || r.Contains("key") || r.Contains("authenticat") || r.Contains("credential") || r.Contains("security"))
            tips.Add("Check the Wi-Fi password: Settings › Network & internet › Wi-Fi › Manage known networks › Forget, then connect again.");
        tips.Add("Move closer to the router, or restart the router if no device can connect.");
        tips.Add(TipWifiDriver);
        string failText = "Windows tried to connect but it failed." + (reason != null ? $" Reason given: {Quote(reason)}." : "");
        return Make(e, "wifi-fail", EvCategory.Network, EvSeverity.Problem, $"Couldn't connect to Wi-Fi network {ssid}", failText, Kinds.WifiFail, tips)
            with { Subject = net, WithNetworkName = net == null ? null : n => ($"Couldn't connect to Wi-Fi network {n}", failText) };
    }

    private static Classified Wlan11006(RawEvent e)
    {
        string? ssid = Val.Str(e.Field("SSID"));
        const string secText = "The PC and the router couldn't agree the Wi-Fi encryption keys, so the connection failed. A wrong password, or a router mixing WPA2 and WPA3 settings, are common causes.";
        return Make(e, "wifi-sec", EvCategory.Network, EvSeverity.Problem, ssid != null ? $"Wi-Fi security handshake failed: {ssid}" : "Wi-Fi security handshake failed",
            secText,
            Kinds.WifiFail, new[]
            {
                "Forget the network and reconnect with the password (Settings › Network & internet › Wi-Fi › Manage known networks).",
                "If the router has a 'WPA2/WPA3 mixed' mode and drops are frequent, try WPA2 only as a test.",
                TipWifiDriver,
            }) with { ShowMessageLine = true, Subject = ssid, WithNetworkName = ssid == null ? null : n => ($"Wi-Fi security handshake failed: {n}", secText) };
    }

    private static Classified IntelWifi(RawEvent e)
    {
        var tips = new[] { TipWifiDriver, TipWifiPower, TipWifiPlan, TipWifiBt };
        switch (e.Id)
        {
            case 6062:                                          // "Lso was triggered" – very common
            case 7021:                                          // connection telemetry
                return Harmless(e, EvCategory.Network, "Intel Wi-Fi driver notice",
                    "A routine notice from the Intel Wi-Fi driver about one of its internal features. Harmless.");
            case 5002:                                          // UNVERIFIED: "has determined that the network adapter is not functioning properly"
                return Make(e, "intelwifi", EvCategory.Network, EvSeverity.Problem, "Intel Wi-Fi adapter stopped working properly",
                    "The Intel Wi-Fi driver decided the adapter wasn't working properly and reset it. Wi-Fi drops for a few seconds when this happens.",
                    Kinds.WifiAdapter, tips) with { ShowMessageLine = true };
            case 5005:                                          // UNVERIFIED: internal error
            case 5007:                                          // UNVERIFIED: TX/CMD timeout (firmware hang)
                return Make(e, "intelwifi", EvCategory.Network, EvSeverity.Problem, "Intel Wi-Fi adapter hit an internal error",
                    "The Wi-Fi adapter's firmware stopped responding and the driver had to recover it. Wi-Fi usually drops briefly.",
                    Kinds.WifiAdapter, tips) with { ShowMessageLine = true };
        }
        return Make(e, "intelwifi-other", EvCategory.Network, EvSeverity.Minor, $"Intel Wi-Fi driver {(e.Level is 1 or 2 ? "error" : "warning")} (event {e.Id})",
            "The Intel Wi-Fi driver logged this. If it lines up with Wi-Fi drops, updating the driver is the first thing to try.",
            null, new[] { TipWifiDriver }) with { ShowMessageLine = true };
    }

    private static Classified Ethernet(RawEvent e)
    {
        bool intel = e.Provider.StartsWith("e1", StringComparison.OrdinalIgnoreCase) || e.Provider.StartsWith("e2f", StringComparison.OrdinalIgnoreCase);
        bool realtek = e.Provider.StartsWith("rt", StringComparison.OrdinalIgnoreCase);
        // Intel e1*/e2f* event 27 = "Network link is disconnected". Realtek appears to use 27 too – UNVERIFIED; the
        // message line is always shown so a wrong guess is visible.
        if (e.Id == 27 && (intel || realtek))
        {
            var tips = new List<string>
            {
                "If you didn't unplug anything: try a different network cable and a different port on the router.",
                "Device Manager › Network adapters › your Ethernet adapter › Advanced: turn off 'Energy Efficient Ethernet' / 'Green Ethernet'; on Power Management untick 'Allow the computer to turn off this device'.",
                $"Update the network driver from {(intel ? "Intel" : "Realtek")} or the motherboard maker.",
            };
            if (e.Provider.StartsWith("e2f", StringComparison.OrdinalIgnoreCase))
                tips.Insert(0, "Intel I225-V / I226-V adapters have known drop-outs: install Intel's latest driver and turn off Energy Efficient Ethernet.");
            return Make(e, "eth-down", EvCategory.Network, EvSeverity.Problem, "Ethernet link lost",
                "The wired network adapter lost its link to the router or switch. If you didn't unplug anything, it's usually the cable, the router restarting, or power saving on the adapter.",
                Kinds.EthDrop, tips) with { NetDrop = true, ShowMessageLine = true };
        }
        bool err = e.Level is 1 or 2;
        return Make(e, "eth-other", EvCategory.Network, err ? EvSeverity.Problem : EvSeverity.Minor,
            $"Network adapter driver {(err ? "error" : "warning")} ({e.Provider}, event {e.Id})",
            "The wired network adapter's driver logged this. If it lines up with connection drops, update the driver.",
            err ? Kinds.NetAdapter : null, new[] { "Update the network driver from the adapter or motherboard maker." }) with { ShowMessageLine = true };
    }

    // =============================================================================================== updates

    private static readonly Dictionary<ulong, string> UpdateErrors = new()
    {
        [0x80070643] = "a general install error (for the recovery-partition update KB5034441 it means that partition is too small)",
        [0x800F0922] = "Windows couldn't finish the install – often a VPN blocking it, or too little space in the system reserved partition",
        [0x80073D02] = "an app the update needed to replace was still running",
        [0x80070002] = "some files the update needed couldn't be found",
        [0x80070003] = "some files the update needed couldn't be found",
        [0x800F081F] = "the files needed to repair Windows weren't found",
        [0x80073712] = "the Windows component store is damaged",
        [0x80070070] = "there isn't enough free disk space",
        [0x80070005] = "access was denied – often security software blocking it",
        [0x80070020] = "a file was in use – often security software scanning it",
        [0x8024200B] = "the installer failed",
        [0x80240034] = "the download failed",
        [0x80246007] = "the download failed",
        [0x8024402C] = "Windows couldn't reach the update server",
        [0x80072EE2] = "the connection to the update server timed out",
        [0x800705B4] = "the operation timed out",
        [0x8007000D] = "the update data was damaged",
        [0x80242016] = "the install was interrupted",
        [0x800F0988] = "the component store needs cleaning up",
        [0x80240017] = "the update doesn't apply to this PC",
    };

    private static Classified Update20(RawEvent e)
    {
        string title = Val.Name(e.Text("updateTitle", 1)) ?? "an update";
        ulong? code = Val.Hex(e.Field("errorCode", 0));
        string err = code is ulong c && c != 0
            ? UpdateErrors.TryGetValue(c, out var meaning) ? $" Error 0x{c:X8}: {meaning}." : $" Error code 0x{c:X8}."
            : "";
        var tips = new List<string>();
        if (code is 0x80073712 or 0x800F081F or 0x8007000D or 0x800F0988) tips.Add(TipSfc);
        if (code == 0x80073D02) tips.Add("Close that app (or restart the PC) and run the update again.");
        if (code == 0x80070070) tips.Add("Free up space on C: (WinScope's Storage tool can help), then try again.");
        if (code == 0x80070643 && title.Contains("KB5034441", StringComparison.OrdinalIgnoreCase))
            tips.Add("This particular update needs a bigger recovery partition. It's safe to skip on most home PCs; Microsoft has published steps if you want it.");
        if (Regex.IsMatch(title, @"^9[A-Z0-9]{11}-") || title.Contains("Microsoft.", StringComparison.Ordinal))
            tips.Add("This is a Microsoft Store app update – open the Store › Library › Get updates to retry.");
        tips.Add("Windows usually tries again by itself – check Settings › Windows Update in a day or so.");
        tips.Add("Run the troubleshooter: Settings › System › Troubleshoot › Other troubleshooters › Windows Update.");
        return Make(e, "wu-fail", EvCategory.Updates, EvSeverity.Problem, $"Update failed to install: {title}",
            "Windows couldn't install this update." + err, Kinds.UpdateFail, tips) with { Subject = title, Code = code ?? 0 };
    }

    private static Classified UpdateInfo(RawEvent e, bool installed)
    {
        string title = Val.Name(e.Text("updateTitle", 0)) ?? "an update";
        return installed
            ? Make(e, "wu-ok", EvCategory.Updates, EvSeverity.Info, $"Update installed: {title}", "Windows installed this update successfully.", null) with { Subject = title }
            : Make(e, "wu-start", EvCategory.Updates, EvSeverity.Info, $"Update started installing: {title}", "Windows started installing this update.", null) with { Subject = title };
    }

    // =============================================================================================== power & startup

    public static string WakeSource(string? raw)
    {
        if (raw == null) return "something Windows didn't name";
        string s = raw.Trim();
        if (s.StartsWith("Device -", StringComparison.OrdinalIgnoreCase)) return s[8..].Trim();
        if (s.StartsWith("Timer -", StringComparison.OrdinalIgnoreCase)) return "a wake timer (a scheduled task or Windows Update)";
        if (s.Equals("Power Button", StringComparison.OrdinalIgnoreCase)) return "the power button";
        if (s.Equals("Unknown", StringComparison.OrdinalIgnoreCase)) return "something Windows didn't name";
        if (s.Contains("Hibernate", StringComparison.OrdinalIgnoreCase)) return "Windows moving from sleep to hibernate";
        return s;
    }

    private static Classified Wake1(RawEvent e)
    {
        string? raw = Val.Name(e.Text("WakeSourceText"));
        string src = WakeSource(raw);
        var tips = new List<string>();
        if (raw != null && raw.StartsWith("Device", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add($"To stop it waking the PC: Device Manager › {src} › Power Management › untick 'Allow this device to wake the computer'.");
            tips.Add("See everything allowed to wake the PC: run powercfg /devicequery wake_armed in a terminal.");
        }
        else if (raw != null && raw.StartsWith("Timer", StringComparison.OrdinalIgnoreCase))
            tips.Add("To stop scheduled wake-ups: Power Options › Change advanced power settings › Sleep › Allow wake timers › Disable.");
        return Make(e, "pt-wake", EvCategory.Power, EvSeverity.Info, $"PC woke from sleep – woken by {src}",
            "The PC resumed from sleep.", null, tips);
    }

    private static Classified KernelBoot27(RawEvent e)
    {
        ulong type = Val.UInt(e.Field("BootType", 0)) ?? 0;
        var (title, text) = type switch
        {
            1 => ("Windows started (Fast Startup)", "Windows started from Fast Startup: 'Shut down' saved the core of Windows to disk and this start resumed it, so drivers weren't fully restarted. Use Restart when you want a completely fresh start."),
            2 => ("Windows resumed from hibernation", "The PC resumed from hibernation."),
            _ => ("Windows started (full start)", "Windows started from scratch."),
        };
        return Make(e, "kb27", EvCategory.Power, EvSeverity.Info, title, text, null) with { Code = type };
    }

    /// <summary>User32 1074 process → who started the restart.</summary>
    public static string ShutdownInitiator(string? process, string? reason)
    {
        if (process == null) return "an app";
        string p = Regex.Replace(process, @"\s*\(.*\)\s*$", "");
        string exe = (Path.GetFileName(p.Replace('/', '\\').Split('\\').Last()) ?? p).ToLowerInvariant();
        string rs = reason?.ToLowerInvariant() ?? "";
        switch (exe)
        {
            case "trustedinstaller.exe":
            case "mousocoreworker.exe":
            case "usoclient.exe":
            case "wuauclt.exe":
            case "musnotification.exe":
            case "musnotificationux.exe":
            case "tiworker.exe":
                return "Windows Update";
            case "explorer.exe":
            case "startmenuexperiencehost.exe":
            case "runtimebroker.exe":
            case "shellexperiencehost.exe":
            case "searchhost.exe":
                return "you (Start menu)";
            case "winlogon.exe":
            case "logonui.exe":
                return "you (sign-in screen or Ctrl+Alt+Del)";
            case "shutdown.exe":
                return "the shutdown command";
            case "svchost.exe":
                return rs.Contains("service pack") || rs.Contains("upgrade") || rs.Contains("hotfix") || rs.Contains("security fix") || rs.Contains("update")
                    ? "Windows Update" : "a Windows service";
            case "msiexec.exe":
                return "an installer (msiexec.exe)";
        }
        return exe.Length > 0 ? exe : "an app";
    }

    private static Classified User32_1074(RawEvent e)
    {
        string? process = e.TextAt(0), reason = Val.Name(e.TextAt(2)), type = e.TextAt(4), comment = e.TextAt(5);
        string who = ShutdownInitiator(process, reason);
        string t = (type ?? "").ToLowerInvariant();
        string what = t.Contains("restart") ? "Restart" : t.Contains("off") || t.Contains("shutdown") ? "Shutdown" : "Restart or shutdown";
        string text = $"{what} was started by {who}" + (process != null ? $" ({Regex.Replace(process, @"\s*\(.*\)\s*$", "")})" : "") + ".";
        if (reason != null) text += $" Reason given: {reason}.";
        if (comment != null) text += $" Comment: {comment}";
        return Make(e, "user32", EvCategory.Power, EvSeverity.Info, $"{what} started by {who}", text, null);
    }

    // =============================================================================================== services

    private static readonly Dictionary<int, string> Win32Errors = new()
    {
        [2] = "its program file couldn't be found",
        [3] = "its program folder couldn't be found",
        [5] = "access was denied",
        [31] = "a device it uses isn't working",
        [193] = "its program file isn't a valid Windows program",
        [577] = "Windows couldn't verify its digital signature",
        [1053] = "it didn't respond in time",
        [1058] = "it's disabled",
        [1067] = "its process ended unexpectedly",
        [1068] = "a service it depends on failed to start",
        [1069] = "it couldn't sign in (wrong account or password)",
        [1275] = "Windows blocked its driver from loading",
        [1455] = "the page file is too small",
    };

    /// <summary>"%%1053" or "1053" → plain English (or null).</summary>
    public static string? ServiceError(string? s)
    {
        if (s == null) return null;
        var m = Regex.Match(s, @"^%%(\d+)$");
        if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, Inv, out int code))
            return Win32Errors.TryGetValue(code, out var t) ? t : $"error {code}";
        return s.StartsWith("%%", StringComparison.Ordinal) ? null : s.TrimEnd('.');
    }

    private static string[] ServiceTips(string name)
    {
        var tips = new List<string>();
        string n = name.ToLowerInvariant();
        if (n.Contains("nvidia")) tips.Add("It's an NVIDIA service – updating or reinstalling the NVIDIA driver/app usually fixes it.");
        tips.Add("If it belongs to a program you installed (NVIDIA, Razer, ASUS, Corsair and so on), update or reinstall that program.");
        tips.Add("A one-off is usually nothing to worry about.");
        return tips.ToArray();
    }

    private static Classified ServiceCrash(RawEvent e)
    {
        string name = Val.Name(e.TextAt(0)) ?? "A";
        string? times = e.TextAt(1);
        string title = e.Id == 7031 ? $"{name} service crashed (Windows restarts it)" : $"{name} service crashed";
        string text = $"The {name} service (a background program) stopped unexpectedly" +
                      (times != null && times != "1" ? $" – it has done this {times} times since start-up." : ".");
        return Make(e, "svc-crash", EvCategory.Services, EvSeverity.Problem, title, text, Kinds.Service, ServiceTips(name)) with { Subject = name };
    }

    private static Classified ServiceFail(RawEvent e)
    {
        switch (e.Id)
        {
            case 7009:
            {
                string name = Val.Name(e.TextAt(1)) ?? "A";
                return Make(e, "svc-fail", EvCategory.Services, EvSeverity.Problem, $"{name} service took too long to start",
                    $"Windows waited {SecondsFromMs(e.TextAt(0))} for the {name} service to start, then gave up. This can slow start-up.", Kinds.Service, ServiceTips(name)) with { Subject = name };
            }
            case 7023:
            {
                string name = Val.Name(e.TextAt(0)) ?? "A";
                string? why = ServiceError(e.TextAt(1));
                return Make(e, "svc-fail", EvCategory.Services, EvSeverity.Problem, $"{name} service stopped with an error",
                    $"The {name} service stopped" + (why != null ? $" because {why}." : " with an error."), Kinds.Service, ServiceTips(name)) with { Subject = name };
            }
            case 7001:
            {
                string name = Val.Name(e.TextAt(0)) ?? "A";
                string? dep = Val.Name(e.TextAt(1));
                return Make(e, "svc-fail", EvCategory.Services, EvSeverity.Problem, $"{name} service couldn't start",
                    $"The {name} service needs {(dep != null ? $"the {dep} service" : "another service")}, which failed to start.", Kinds.Service, ServiceTips(name)) with { Subject = name };
            }
            default:
            {
                string name = Val.Name(e.TextAt(0)) ?? "A";
                string? why = ServiceError(e.TextAt(1));
                return Make(e, "svc-fail", EvCategory.Services, EvSeverity.Problem, $"{name} service failed to start",
                    $"The {name} service couldn't start" + (why != null ? $" because {why}." : "."), Kinds.Service, ServiceTips(name)) with { Subject = name };
            }
        }
    }

    private static string SecondsFromMs(string? ms) =>
        ms != null && long.TryParse(ms, NumberStyles.Integer, Inv, out long v) && v > 0 ? $"{v / 1000} seconds" : "a while";

    // =============================================================================================== harmless & other

    private static Classified Harmless(RawEvent e, EvCategory cat, string title, string explain) =>
        Make(e, "harmless", cat, e.Level is 1 or 2 ? EvSeverity.Problem : EvSeverity.Minor, title, explain, null)
            with { Harmless = true };

    /// <summary>Everything else at Error/Critical: the title becomes the message's first line once it's been formatted.</summary>
    private static Classified Other(RawEvent e) =>
        Make(e, "other", EvCategory.Other, e.Level == 1 ? EvSeverity.Problem : EvSeverity.Minor,
            $"{e.Provider} reported {(e.Level == 1 ? "a critical error" : "an error")} (event {e.Id.ToString(CultureInfo.InvariantCulture)})",
            "Windows logged this error but WinScope doesn't have a plain-English explanation for it. Most one-off errors like this are harmless; " +
            "if it repeats a lot or lines up with a problem you noticed, search the web for the source name and event ID.",
            Kinds.Other) with { MessageIsTitle = true };
}
