using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using WinScope.UI;

namespace WinScope.Modules.Performance;

/// <summary>Colour per spike kind (kept apart from <see cref="SpikeText"/> so the wording stays WPF-free and testable).</summary>
internal static class SpikeColors
{
    public static Color Of(SpikeKind k) => k == SpikeKind.Burst ? Palette.Weak : k == SpikeKind.Memory ? Palette.Rx : k == SpikeKind.Disk ? Palette.Fair : Palette.Bad;
}

/// <summary>One row of the "Right now" table. Raises change notifications only for values that changed.</summary>
public sealed class AppRow : INotifyPropertyChanged
{
    public AppRow(string name) { Name = name; }
    public event PropertyChangedEventHandler? PropertyChanged;

    public string Name { get; }
    public int Count { get; private set; }
    public string CountText { get; private set; } = "";
    public double Cpu { get; private set; }
    public string CpuText { get; private set; } = "";
    public double Gpu { get; private set; }
    public string GpuText { get; private set; } = "";
    public long Ram { get; private set; }
    public string RamText { get; private set; } = "";
    public double Io { get; private set; }
    public string IoText { get; private set; } = "";
    public long VRam { get; private set; }
    public string VRamText { get; private set; } = "";

    internal void Update(in AppNow a)
    {
        if (Count != a.Count) { Count = a.Count; Raise(nameof(Count)); Set(a.Count > 1 ? "×" + a.Count.ToString(CultureInfo.InvariantCulture) : "", CountText, v => CountText = v, nameof(CountText)); }
        double cpu = double.IsNaN(a.Cpu) ? -1 : Math.Round(a.Cpu, 1);
        if (cpu != Cpu) { Cpu = cpu; Raise(nameof(Cpu)); Set(cpu < 0 ? "—" : Pct(a.Cpu), CpuText, v => CpuText = v, nameof(CpuText)); }
        double gpu = double.IsNaN(a.Gpu) ? -1 : Math.Round(a.Gpu, 1);
        if (gpu != Gpu) { Gpu = gpu; Raise(nameof(Gpu)); Set(gpu < 0 ? "—" : Pct(a.Gpu), GpuText, v => GpuText = v, nameof(GpuText)); }
        long ram = a.Ram / (64 * 1024) * (64 * 1024);   // ignore sub-64 KB jitter
        if (ram != Ram || RamText.Length == 0) { Ram = ram; Raise(nameof(Ram)); Set(Format.Bytes(a.Ram), RamText, v => RamText = v, nameof(RamText)); }
        double io = double.IsNaN(a.Io) ? -1 : Math.Round(a.Io / 1024) * 1024;
        if (io != Io || IoText.Length == 0) { Io = io; Raise(nameof(Io)); Set(io < 0 ? "—" : a.Io < 1024 ? "0" : Format.Rate(a.Io), IoText, v => IoText = v, nameof(IoText)); }
        long vram = a.VRam < 0 ? -1 : a.VRam / (1024 * 1024) * (1024 * 1024);
        if (vram != VRam || VRamText.Length == 0) { VRam = vram; Raise(nameof(VRam)); Set(vram <= 0 ? "—" : Format.Bytes(a.VRam), VRamText, v => VRamText = v, nameof(VRamText)); }
    }

    internal static string Pct(double v) => v < 0.05 ? "0%" : Format.Pct(v, v < 10 ? 1 : 0);

    private void Set(string value, string old, Action<string> assign, string prop)
    {
        if (value == old) return;
        assign(value);
        Raise(prop);
    }

    private void Raise([CallerMemberName] string? p = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

/// <summary>One row of the spike list.</summary>
public sealed class SpikeRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public int Id { get; }
    internal SpikeInfo Info { get; private set; }
    public string Time { get; private set; } = "";
    public string KindText { get; private set; } = "";
    public string PeakText { get; private set; } = "";
    public string DurationText { get; private set; } = "";
    public string Culprit { get; private set; } = "";
    public Brush KindBrush { get; private set; } = Brushes.Transparent;

    internal SpikeRow(SpikeInfo s)
    {
        Id = s.Id;
        Info = s;
        Apply(s, force: true);
    }

    internal void Update(SpikeInfo s)
    {
        if (ReferenceEquals(s, Info) || s == Info) return;
        Info = s;
        Apply(s, force: false);
    }

    private void Apply(SpikeInfo s, bool force)
    {
        string time = SpikeText.Time(s.StartUtc);
        var l = PerfClock.ToLocal(s.StartUtc);
        if (l.Date != DateTime.Today) time = l.ToString("ddd ", CultureInfo.InvariantCulture) + time;
        string kind = SpikeText.Kind(s.Kind);
        string peak = SpikeText.Peak(s);
        string dur = s.Ongoing ? "now · " + Format.Span(s.Duration) : Format.Span(s.Duration);
        string culprit = SpikeText.Culprit(s);
        if (force || time != Time) { Time = time; Raise(nameof(Time)); }
        if (force || kind != KindText) { KindText = kind; Raise(nameof(KindText)); }
        if (force || peak != PeakText) { PeakText = peak; Raise(nameof(PeakText)); }
        if (force || dur != DurationText) { DurationText = dur; Raise(nameof(DurationText)); }
        if (force || culprit != Culprit) { Culprit = culprit; Raise(nameof(Culprit)); }
        if (force) { KindBrush = Palette.B(SpikeColors.Of(s.Kind)); Raise(nameof(KindBrush)); }
    }

    private void Raise(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
