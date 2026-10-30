using System.Globalization;
using WinScope.UI;

namespace WinScope.Modules.Performance;

/// <summary>Plain-English wording for spikes, shared by the graphs, the list, the panel and the dashboard card.</summary>
internal static class SpikeText
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string Kind(SpikeKind k) => k switch
    {
        SpikeKind.Cpu => "CPU maxed out",
        SpikeKind.Memory => "Memory nearly full",
        SpikeKind.Disk => "Disk flat out",
        _ => "Sudden CPU burst",
    };

    /// <summary>Value of a culprit in the unit of the spike's kind.</summary>
    public static string Value(SpikeKind k, double v) => k switch
    {
        SpikeKind.Memory => Format.Bytes(v),
        SpikeKind.Disk => Format.Rate(v),
        _ => Format.Pct(v),
    };

    public static string Unit(SpikeKind k) => k switch { SpikeKind.Memory => "RAM", SpikeKind.Disk => "I/O", _ => "CPU" };

    /// <summary>"chrome.exe" for a burst, otherwise the biggest culprit (or null).</summary>
    public static Culprit? MainCulprit(SpikeInfo s) =>
        s.Culprits.Length > 0 ? s.Culprits[0] : s.App != null ? new Culprit(s.App, s.Peak, s.Peak) : null;

    /// <summary>One sentence, e.g. "CPU at 85 %+ for 12s (peak 97%)".</summary>
    public static string Describe(SpikeInfo s)
    {
        string dur = Format.Span(s.Duration);
        return s.Kind switch
        {
            SpikeKind.Cpu => $"CPU at {SpikeDetector.CpuThreshold:0}%+ for {dur} (peak {Format.Pct(s.Peak)})",
            SpikeKind.Memory => $"Memory over {SpikeDetector.MemThreshold:0}% full for {dur} (peak {Format.Pct(s.Peak)})",
            SpikeKind.Disk => $"Disk over {SpikeDetector.DiskThreshold:0}% busy for {dur}",
            _ => $"{s.App} suddenly jumped to {Cores(s.Cores(s.Peak))} ({Format.Pct(s.Peak)} of the whole CPU)",
        };
    }

    /// <summary>"chrome.exe 85% CPU" – the main culprit and its peak during the spike.</summary>
    public static string Culprit(SpikeInfo s)
    {
        if (s.Kind == SpikeKind.Burst) return $"{s.App} {Cores(s.Cores(s.Peak))}";
        if (MainCulprit(s) is not Culprit c) return "no single app stood out";
        return $"{c.Name} {Value(s.Kind, c.Max)} {Unit(s.Kind)}";
    }

    /// <summary>"2.3 cores" – busy logical processors.</summary>
    public static string Cores(double c) => c < 0.05 ? "under 0.1 cores"
        : c.ToString(c < 9.95 ? "0.0" : "0", Inv) + (Math.Abs(c - 1) < 0.05 ? " core" : " cores");

    /// <summary>The spike's peak in its natural unit: cores for a burst, % otherwise.</summary>
    public static string Peak(SpikeInfo s) => s.Kind == SpikeKind.Burst ? Cores(s.Cores(s.Peak)) : Format.Pct(s.Peak);

    public static string Time(DateTime stamp) => PerfClock.ToLocal(stamp).ToString("HH:mm:ss", Inv);
    public static string ShortTime(DateTime utc)
    {
        var l = PerfClock.ToLocal(utc);
        return l.Date == DateTime.Today ? l.ToString("HH:mm", Inv) : l.ToString("ddd HH:mm", Inv);
    }
}
