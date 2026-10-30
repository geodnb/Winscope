using System.Globalization;
using System.Text;
using WinScope.UI;

namespace WinScope.Modules.Performance;

/// <summary>Everything the Performance report needs, gathered by the module from data it already holds.</summary>
internal sealed class PerfReportInput
{
    /// <summary>Now, on the tool's timeline (<see cref="PerfClock.Now"/>).</summary>
    public DateTime NowStamp;
    /// <summary>Timeline stamp → local wall-clock time (<see cref="PerfClock.ToLocal"/>).</summary>
    public Func<DateTime, DateTime> ToLocal = t => t.ToLocalTime();
    public WindowStats? Window;
    public LiveData? Latest;
    public SpikeInfo[] Spikes = Array.Empty<SpikeInfo>();
    /// <summary>Applied to the finished text when the report is redacted (Redactor.Apply); null = keep as is.</summary>
    public Func<string, string>? Redact;
}

/// <summary>
/// Builds the Performance part of a WinScope report: plain text for someone (or an AI assistant) who can't see
/// the screen. Pure – no I/O, no locks of its own – so it's quick and unit-tested.
/// </summary>
internal static class PerfReport
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    public const int MaxSpikes = 15, MaxCpuApps = 8, MaxOtherApps = 5, MaxNow = 5;
    private const int MaxName = 40;

    public static (List<string> findings, string body) Build(PerfReportInput i)
    {
        var findings = new List<string>();
        var w = i.Window;
        var d = i.Latest;
        if (w == null || w.Samples < 2 || d == null)
        {
            const string wait = "Not available yet – WinScope has only just started recording (it takes a reading every second).";
            return (findings, wait);
        }

        int cpus = Math.Max(1, d.Cpus);
        double perCore = 100.0 / cpus;
        var today = i.ToLocal(i.NowStamp).Date;
        string When(DateTime stamp)
        {
            var l = i.ToLocal(stamp);
            return l.ToString(l.Date == today ? "HH:mm" : "ddd HH:mm", Inv);
        }
        string Cores(double pct) => SpikeText.Cores(pct / perCore);
        string Secs(int s) => s <= 0 ? "never" : Format.Span(TimeSpan.FromSeconds(s));
        var span = w.ToUtc - w.FromUtc + TimeSpan.FromSeconds(1);
        string windowText = "the last " + Format.Span(span);

        var sb = new StringBuilder();
        // Redact each whole line BEFORE wrapping, so a path or address is never split across lines unredacted.
        string R(string text) => i.Redact != null ? i.Redact(text) : text;
        string Clean(string raw, int max) => Trim(R(ExeOnly(raw)), max);
        void L(string s) { foreach (var line in Wrap(R(s))) sb.Append(line).Append('\n'); }

        // ---------------------------------------------------------------- coverage
        L($"Covers {When(w.FromUtc)}–{When(w.ToUtc)} ({Format.Span(span)}), one reading per second. "
          + "WinScope keeps the last 2 hours while it's running.");
        int missing = (int)span.TotalSeconds - w.Samples;
        if (missing >= 60) L($"About {Format.Span(TimeSpan.FromSeconds(missing))} of that has no readings (e.g. the PC was asleep).");
        L($"CPU figures are % of the whole processor: {cpus} logical processors = 100%, so 1 core = {perCore.ToString(perCore < 10 ? "0.0" : "0", Inv)}%.");
        if (!d.Supported) L("Per-app, GPU and disk figures aren't available on this build of WinScope (they need the 64-bit version).");

        // ---------------------------------------------------------------- totals
        var t = d.Totals;
        L("");
        L("Overall:");
        L($"- CPU: now {Format.Pct(t.Cpu)}, average {Format.Pct(w.Cpu.Avg)}, peak {PeakText(w.Cpu, When)}. "
          + $"Above {SpikeDetector.CpuThreshold:0}%: {Secs(w.CpuOver85)}.");

        if (d.Gpu == GpuStatus.Unavailable)
            L(d.Supported ? "- GPU: not available on this PC (GPU usage needs a WDDM 2 graphics driver)." : "- GPU: not available on this build.");
        else if (w.GpuSamples == 0)
            L("- GPU: no readings yet.");
        else
        {
            // Adapter model name only (from the registry "Description"); LUIDs / device IDs are never printed.
            string name = d.GpuName is string gn ? Trim(R(gn), 60) : "main graphics adapter";
            string size = d.GpuVramTotal > 0 ? $" ({Format.Bytes(d.GpuVramTotal)} video memory)" : "";
            L($"- GPU – {name}{size}: now {Format.Pct(t.Gpu)}, average {Format.Pct(w.Gpu.Avg)}, peak {PeakText(w.Gpu, When)}.");
            if (!double.IsNaN(d.GpuVramUsed)) L($"  Video memory in use now: {Format.Bytes(d.GpuVramUsed)}" + (d.GpuVramTotal > 0 ? $" of {Format.Bytes(d.GpuVramTotal)}." : "."));
            if (d.GpuAdapterCount > 1)
            {
                int others = d.GpuAdapterCount - 1;
                string busy = w.GpuOther.N > 0 && w.GpuOther.Peak >= 1 ? $"its busiest moment was {Format.Pct(w.GpuOther.Peak)}" : "it was idle";
                L(others == 1
                    ? $"  1 other graphics adapter (e.g. integrated graphics) isn't included above; {busy}."
                    : $"  {others} other graphics adapters (e.g. integrated graphics) aren't included above; {busy.Replace("its", "their")}.");
            }
            if (d.GpuEvery > 1) L($"  (GPU counters are slow to read on this PC, so GPU is read every {d.GpuEvery} s.)");
            L("  A GPU near 100% while gaming is normal and isn't flagged as a problem.");
        }

        string ramNow = t.RamTotal > 0 ? $"{Format.Bytes(t.RamUsed)} of {Format.Bytes(t.RamTotal)} ({Format.Pct(t.RamPct)})" : Format.Pct(t.RamPct);
        L($"- RAM: now {ramNow}, average {Format.Pct(w.Ram.Avg)}, peak {PeakText(w.Ram, When)}"
          + (w.PeakRamUsed > 0 ? $" ({Format.Bytes(w.PeakRamUsed)})" : "") + $". Above {SpikeDetector.MemThreshold:0}%: {Secs(w.RamOver90)}.");
        double commitPct = d.CommitLimit > 0 ? d.CommitUsed * 100.0 / d.CommitLimit : double.NaN;
        if (!double.IsNaN(commitPct))
            L($"- Commit charge (memory promised to apps: RAM + page file): {Format.Bytes(d.CommitUsed)} of {Format.Bytes(d.CommitLimit)} ({Format.Pct(commitPct)}).");
        if (!d.DiskAvailable && w.Disk.N == 0) L("- Disk activity: not available on this PC.");
        else
        {
            L($"- Disk active (busiest physical disk): now {Format.Pct(t.Disk)}, average {Format.Pct(w.Disk.Avg)}, peak {PeakText(w.Disk, When)}. "
              + $"Above {SpikeDetector.DiskThreshold:0}% busy: {Secs(w.DiskOver90)}.");
            if (!double.IsNaN(t.DiskRead)) L($"  All disks now: reading {Format.Rate(t.DiskRead)}, writing {Format.Rate(t.DiskWrite)}.");
        }

        // ---------------------------------------------------------------- top apps over the window
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in d.Apps) counts[a.Name] = a.Count;
        string N(string name) => $"{Clean(name, MaxName)}{(counts.TryGetValue(name, out int c) && c > 1 ? $" ×{c}" : "")}";
        int procN = Math.Max(1, w.ProcSamples), gpuN = Math.Max(1, w.GpuSamples);

        if (d.Supported && w.ProcSamples > 0)
        {
            L("");
            L($"Apps that used the most over {windowText} (average, then peak; ×N = processes running now):");
            L("- CPU:");
            int shown = 0;
            foreach (var a in w.TopCpu)
            {
                double avg = a.Sum / procN;
                if (avg < 0.1 || shown >= MaxCpuApps) continue;
                L($"  - {N(a.Name)}: {Pct1(avg)} ({Cores(avg)}), peak {Pct1(a.Max)} ({Cores(a.Max)})");
                shown++;
            }
            if (shown == 0) L("  - nothing used noticeable CPU");
            if (d.Gpu == GpuStatus.Available && w.TopGpu.Count > 0)
            {
                L("- GPU (main adapter):");
                foreach (var a in w.TopGpu.Take(MaxOtherApps)) L($"  - {N(a.Name)}: {Pct1(a.Sum / gpuN)}, peak {Pct1(a.Max)}");
            }
            L("- Memory (private RAM, as Task Manager shows):");
            foreach (var a in w.TopRam.Take(MaxOtherApps)) L($"  - {N(a.Name)}: {Format.Bytes(a.Sum / procN)} average, peak {Format.Bytes(a.Max)}");
            if (w.TopIo.Count > 0)
            {
                L("- I/O (disk reads/writes plus network and other device traffic):");
                foreach (var a in w.TopIo.Take(MaxOtherApps)) L($"  - {N(a.Name)}: {Format.Rate(a.Sum / procN)} average, peak {Format.Rate(a.Max)}");
            }

            // right now
            var now = d.Apps.Where(a => !double.IsNaN(a.Cpu)).OrderByDescending(a => a.Cpu).Take(MaxNow).ToList();
            if (now.Count > 0)
            {
                L("");
                L($"Right now, busiest by CPU ({d.Apps.Length} apps, {d.ProcessCount} processes running):");
                foreach (var a in now)
                {
                    string gpu = double.IsNaN(a.Gpu) ? "" : $", GPU {Pct1(a.Gpu)}";
                    string io = double.IsNaN(a.Io) ? "" : $", I/O {Format.Rate(a.Io)}";
                    L($"- {N(a.Name)}: CPU {Pct1(a.Cpu)} ({Cores(a.Cpu)}){gpu}, RAM {Format.Bytes(a.Ram)}{io}");
                }
            }
        }

        // ---------------------------------------------------------------- spikes
        L("");
        if (i.Spikes.Length == 0)
            L("Spikes: none recorded. (WinScope flags CPU over 85% for 3 s, memory over 90% for 5 s, a disk over 90% busy "
              + "for 5 s, and any app jumping from under 0.5 to 2+ cores within 2 s.)");
        else
        {
            L($"Spikes, newest first ({i.Spikes.Length} recorded; flagged: CPU over 85% for 3 s, memory over 90% for 5 s,");
            L("a disk over 90% busy for 5 s, or one app jumping from under 0.5 to 2+ cores within 2 s):");
            foreach (var s in i.Spikes.Take(MaxSpikes))
            {
                string head = $"- {When(s.StartUtc)}, for {Format.Span(s.Duration)}{(s.Ongoing ? " (still happening)" : "")}: ";
                switch (s.Kind)
                {
                    case SpikeKind.Burst:
                        L(head + $"{Clean(s.App ?? "an app", MaxName)} suddenly jumped to {SpikeText.Cores(s.Cores(s.Peak))} ({Format.Pct(s.Peak)} of the CPU).");
                        break;
                    case SpikeKind.Cpu:
                        L(head + $"CPU maxed out, peak {Format.Pct(s.Peak)}.");
                        if (s.Culprits.Length > 0)
                            L("  Main users (cores, average / peak): " + string.Join(", ", s.Culprits.Take(3).Select(c =>
                                $"{Clean(c.Name, 30)} {Num(s.Cores(c.Avg))} / {Num(s.Cores(c.Max))}")));
                        break;
                    case SpikeKind.Memory:
                        L(head + $"memory over 90% full, peak {Format.Pct(s.Peak)}.");
                        if (s.Culprits.Length > 0)
                            L("  Biggest users: " + string.Join(", ", s.Culprits.Take(3).Select(c => $"{Clean(c.Name, 30)} {Format.Bytes(c.Max)}")));
                        break;
                    default:
                        L(head + $"a disk was flat out (over 90% busy), peak {Format.Pct(s.Peak)}.");
                        if (s.Culprits.Length > 0)
                            L("  Most I/O (average / peak): " + string.Join(", ", s.Culprits.Take(3).Select(c => $"{Clean(c.Name, 30)} {Format.Rate(c.Avg)} / {Format.Rate(c.Max)}")));
                        break;
                }
            }
            if (i.Spikes.Length > MaxSpikes) L($"- and {i.Spikes.Length - MaxSpikes} older spike(s)");
        }

        L("");
        L(double.IsNaN(d.SelfCpu)
            ? "WinScope's own CPU use: not measured yet."
            : $"WinScope's own CPU use: {Pct1(d.SelfCpu)} right now (it reads all of this once a second).");

        // ---------------------------------------------------------------- findings (most serious first)
        var inWindow = i.Spikes.Where(s => s.EndUtc >= w.FromUtc).ToList();
        // Blame the app that was furthest above its own usual level during the spikes (a game that always uses
        // 3 cores isn't the cause of a CPU spike; the antivirus scan that jumped from 0 to 3 cores is).
        double Usual(List<AppStat> list, string name, int samples)
        {
            foreach (var a in list) if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return a.Sum / Math.Max(1, samples);
            return 0;
        }
        if (w.RamOver90 >= 5 || t.RamPct >= SpikeDetector.MemThreshold)
        {
            var mb = Blame(inWindow.Where(s => s.Kind == SpikeKind.Memory), _ => 0, useMax: true);
            string who = mb is { } m ? $"; biggest user '{Clean(m.name, MaxName)}' ({Format.Bytes(m.value)})"
                : w.TopRam.Count > 0 ? $"; biggest user '{Clean(w.TopRam[0].Name, MaxName)}' (up to {Format.Bytes(w.TopRam[0].Max)})" : "";
            findings.Add(w.RamOver90 >= 5
                ? $"Memory was over 90% full for {Secs(w.RamOver90)} in {windowText} (peak {Format.Pct(w.Ram.Peak)}){who}"
                : $"Memory is {Format.Pct(t.RamPct)} full right now{who}");
        }
        if (commitPct >= 90)
            findings.Add($"Commit charge is at {Format.Pct(commitPct)} of its limit – apps may fail to get memory (page file may be too small)");
        if (w.CpuOver85 >= 30 || inWindow.Any(s => s.Kind == SpikeKind.Cpu))
        {
            var blame = Blame(inWindow.Where(s => s.Kind == SpikeKind.Cpu), n => Usual(w.TopCpu, n, procN), useMax: false);
            string who = blame is { } b ? $", mostly '{Clean(b.name, MaxName)}' at {SpikeText.Cores(b.value / perCore)}"
                + $" (usually {SpikeText.Cores(Usual(w.TopCpu, b.name, procN) / perCore)})"
                : w.TopCpu.Count > 0 ? $", mostly '{Clean(w.TopCpu[0].Name, MaxName)}'" : "";
            findings.Add($"CPU was above 85% for {Secs(w.CpuOver85)} in {windowText}{who}");
        }
        if (w.DiskOver90 >= 5)
        {
            var blame = Blame(inWindow.Where(s => s.Kind == SpikeKind.Disk), n => Usual(w.TopIo, n, procN), useMax: false);
            string who = blame is { } b ? $", most I/O from '{Clean(b.name, MaxName)}' ({Format.Rate(b.value)})" : "";
            findings.Add($"A disk was over 90% busy for {Secs(w.DiskOver90)} in {windowText}{who}");
        }
        var bursts = i.Spikes.Where(s => s.Kind == SpikeKind.Burst && s.App != null).ToList();
        if (bursts.Count > 0)
        {
            var top = bursts.GroupBy(s => s.App!, StringComparer.OrdinalIgnoreCase).OrderByDescending(g => g.Count()).First();
            var latest = bursts[0];
            string what = bursts.Count == 1 ? "1 sudden CPU burst" : $"{bursts.Count} sudden CPU bursts";
            string who = top.Count() > 1 ? $"most often '{Clean(top.Key, MaxName)}' (×{top.Count()}), " : "";
            findings.Add($"{what} (an app jumping to 2+ cores within 2 s – a common cause of game stutter); {who}"
                + $"latest {When(latest.StartUtc)}: '{Clean(latest.App!, MaxName)}' at {SpikeText.Cores(latest.Cores(latest.Peak))}");
        }
        if (findings.Count == 0)
            findings.Add($"No CPU, memory or disk spikes in {windowText}");
        if (findings.Count > 5) findings.RemoveRange(5, findings.Count - 5);

        string body = sb.ToString();
        if (i.Redact != null)
        {
            // Values were already redacted before trimming/wrapping; this whole-text pass is only a safety net.
            body = i.Redact(body);
            for (int k = 0; k < findings.Count; k++) findings[k] = i.Redact(findings[k]);
        }
        return (findings, body);
    }

    /// <summary>
    /// The app with the most (excess over its usual level × duration) across these spikes, and its value during
    /// them (average, or peak with <paramref name="useMax"/>).
    /// </summary>
    private static (string name, double value)? Blame(IEnumerable<SpikeInfo> spikes, Func<string, double> usual, bool useMax)
    {
        var score = new Dictionary<string, (double weight, double value)>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in spikes)
            foreach (var c in s.Culprits)
            {
                double secs = Math.Max(1, s.Duration.TotalSeconds);
                double excess = Math.Max(0, (useMax ? c.Max : c.Avg) - usual(c.Name)) + 1e-9;
                var cur = score.GetValueOrDefault(c.Name);
                score[c.Name] = (cur.weight + excess * secs, Math.Max(cur.value, useMax ? c.Max : c.Avg));
            }
        if (score.Count == 0) return null;
        var best = score.OrderByDescending(kv => kv.Value.weight).First();
        return (best.Key, best.Value.value);
    }

    private static string PeakText(Stat s, Func<DateTime, string> when) => s.N == 0 ? "—" : $"{Format.Pct(s.Peak)} at {when(s.PeakAt)}";

    private static string Num(double v) => v.ToString(v < 9.95 ? "0.0" : "0", Inv);

    /// <summary>Keep lines readable (≤ 110 characters): break long ones at ", " / "; " / " " with an indent.</summary>
    internal static IEnumerable<string> Wrap(string line, int max = 110)
    {
        if (line.Length <= max) { yield return line; yield break; }
        int lead = 0;
        while (lead < line.Length && (line[lead] == ' ' || line[lead] == '-')) lead++;
        string indent = new(' ', Math.Min(lead + 2, 12));
        string rest = line;
        bool first = true;
        while (rest.Length > max)
        {
            int cut = -1;
            foreach (var sep in new[] { ", ", "; ", ". ", " " })
            {
                int k = rest.LastIndexOf(sep, max - 1, StringComparison.Ordinal);
                if (k > indent.Length + 10) { cut = k + sep.Length - (sep == " " ? 1 : 0); break; }
            }
            if (cut <= 0) cut = max;
            yield return rest[..cut].TrimEnd();
            rest = indent + rest[cut..].TrimStart();
            first = false;
        }
        if (!first || rest.Length > 0) yield return rest;
    }

    private static string Pct1(double v) => v < 0.05 ? "0%" : Format.Pct(v, v < 10 ? 1 : 0);

    /// <summary>
    /// App names are image (exe) names, never paths – but if a full path ever arrives (e.g.
    /// C:\Users\x\Downloads\Payslip-viewer.exe) keep only the file name, so no folder or user name can leak.
    /// </summary>
    internal static string ExeOnly(string name)
    {
        int k = name.LastIndexOfAny(new[] { '\\', '/' });
        return k >= 0 && k < name.Length - 1 ? name[(k + 1)..] : name;
    }

    internal static string Trim(string s, int max)
    {
        s = s.Replace('\n', ' ').Replace('\r', ' ');
        return s.Length <= max ? s : s[..(max - 1)] + "…";
    }
}
