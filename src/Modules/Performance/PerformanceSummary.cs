using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using WinScope.UI;

namespace WinScope.Modules.Performance;

/// <summary>
/// Dashboard card body: big CPU / GPU / RAM figures with 2-minute sparklines, the busiest app right now and the
/// most recent spike. Fits the smallest card (≈446×245) and uses the extra room of a big one (≈900×380): a Disk
/// tile when wide, taller sparklines plus "most GPU / most memory" lines when tall; optional bits hide when short.
/// Clicks anywhere open the full view (handled by the shell).
/// </summary>
internal sealed class PerformanceSummary : Grid
{
    private readonly TextBlock _cpu, _gpu, _ram, _disk, _cpuSub, _gpuSub, _ramSub, _diskSub;
    private readonly Sparkline _cpuSpark, _gpuSpark, _diskSpark;
    private readonly UsageBar _ramBar;
    private readonly Border _diskTile;
    private readonly ColumnDefinition _diskGap, _diskCol;
    private readonly TextBlock _busiest, _lastSpike, _topGpu, _topRam;
    private bool _wide, _tall;

    private const double WideAt = 680, TallAt = 320, ShortAt = 215, TinyAt = 175;

    public PerformanceSummary()
    {
        for (int i = 0; i < 5; i++) RowDefinitions.Add(new RowDefinition { Height = i == 1 ? new GridLength(12) : GridLength.Auto });

        var tiles = new Grid();
        for (int i = 0; i < 5; i++)
            tiles.ColumnDefinitions.Add(new ColumnDefinition { Width = i % 2 == 1 ? new GridLength(10) : new GridLength(1, GridUnitType.Star) });
        _diskGap = new ColumnDefinition { Width = new GridLength(0) };
        _diskCol = new ColumnDefinition { Width = new GridLength(0) };
        tiles.ColumnDefinitions.Add(_diskGap);
        tiles.ColumnDefinitions.Add(_diskCol);
        Children.Add(tiles);

        _cpuSpark = Spark(Palette.Accent);
        _gpuSpark = Spark(Palette.Accent2);
        _diskSpark = Spark(Palette.Fair);
        _ramBar = new UsageBar { Fixed = Palette.Rx, Margin = new Thickness(0, 17, 0, 15) };
        tiles.Children.Add(Tile(0, "CPU", Palette.Accent, out _cpu, _cpuSpark, out _cpuSub));
        tiles.Children.Add(Tile(2, "GPU", Palette.Accent2, out _gpu, _gpuSpark, out _gpuSub));
        tiles.Children.Add(Tile(4, "RAM", Palette.Rx, out _ram, _ramBar, out _ramSub));
        _diskTile = Tile(6, "DISK", Palette.Fair, out _disk, _diskSpark, out _diskSub);
        _diskTile.Visibility = Visibility.Collapsed;
        tiles.Children.Add(_diskTile);

        _busiest = Line(2);
        _lastSpike = Line(3);
        var extra = new StackPanel();
        SetRow(extra, 4);
        Children.Add(extra);
        _topGpu = Line(-1); extra.Children.Add(_topGpu);
        _topRam = Line(-1); extra.Children.Add(_topRam);
        _topGpu.Visibility = _topRam.Visibility = Visibility.Collapsed;
        _busiest.Inlines.Add(new Run("Collecting the first samples…") { Foreground = Palette.B(Palette.Muted) });

        SizeChanged += (_, _) => Layout();
    }

    private static Sparkline Spark(Color c) => new() { Height = 34, Stroke = c, Margin = new Thickness(0, 4, 0, 2) };

    private TextBlock Line(int row)
    {
        var t = new TextBlock { FontSize = 13, Margin = new Thickness(2, 3, 0, 3), TextTrimming = TextTrimming.CharacterEllipsis };
        if (row >= 0) { SetRow(t, row); Children.Add(t); }
        return t;
    }

    /// <summary>Show or hide optional parts so nothing clips in a small card and big cards aren't half empty.</summary>
    private void Layout()
    {
        double w = ActualWidth, h = ActualHeight;
        if (w <= 0 || h <= 0) return;
        _wide = w >= WideAt;
        _tall = h >= TallAt;
        _diskTile.Visibility = _wide ? Visibility.Visible : Visibility.Collapsed;
        _diskGap.Width = new GridLength(_wide ? 10 : 0);
        _diskCol.Width = _wide ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        var subVis = h >= ShortAt ? Visibility.Visible : Visibility.Collapsed;
        foreach (var t in new[] { _cpuSub, _gpuSub, _ramSub, _diskSub }) t.Visibility = subVis;
        var chartVis = h >= TinyAt ? Visibility.Visible : Visibility.Collapsed;
        double sparkH = _tall ? 56 : 34;
        // (GPU/Disk sparklines are also hidden by Refresh while their figures aren't available.)
        foreach (var s in new[] { _cpuSpark, _gpuSpark, _diskSpark }) { s.Height = sparkH; s.Visibility = chartVis; }
        _ramBar.Visibility = chartVis;
        _ramBar.Margin = new Thickness(0, (sparkH + 6 - 8) / 2, 0, (sparkH + 6 - 8) / 2);
        _topGpu.Visibility = _topRam.Visibility = _tall ? Visibility.Visible : Visibility.Collapsed;
        _chartsHidden = chartVis == Visibility.Collapsed;
    }

    private bool _chartsHidden;

    private static Border Tile(int col, string label, Color c, out TextBlock value, FrameworkElement chart, out TextBlock sub)
    {
        var sp = new StackPanel();
        var head = new DockPanel();
        var dot = new Border { Width = 7, Height = 7, CornerRadius = new CornerRadius(3.5), Background = Palette.B(c), Margin = new Thickness(0, 0, 6, 0), VerticalAlignment = VerticalAlignment.Center };
        head.Children.Add(dot);
        head.Children.Add(new TextBlock { Text = label, FontSize = 11.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.B(Palette.Muted) });
        sp.Children.Add(head);
        value = new TextBlock { Text = "—", FontSize = 30, FontWeight = FontWeights.SemiBold, Foreground = Palette.B(Palette.Text), Margin = new Thickness(0, 2, 0, 0) };
        sp.Children.Add(value);
        sp.Children.Add(chart);
        sub = new TextBlock { Text = " ", FontSize = 11, Foreground = Palette.B(Palette.Muted), TextTrimming = TextTrimming.CharacterEllipsis };
        sp.Children.Add(sub);
        var b = new Border
        {
            Background = Palette.B(Palette.Panel2), BorderBrush = Palette.B(Palette.Line), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 8), Child = sp,
        };
        Grid.SetColumn(b, col);
        return b;
    }

    internal void Refresh(LiveData? d, History history, SpikeInfo? last)
    {
        if (d == null) return;
        var now = PerfClock.Now;
        var t = d.Totals;
        _cpu.Text = Format.Pct(t.Cpu);
        _cpuSpark.SetPoints(history.Recent(120, now, x => x.Cpu));
        _cpuSub.Text = "last 2 minutes";

        bool gpuOk = d.Gpu != GpuStatus.Unavailable;
        _gpu.Text = gpuOk ? Format.Pct(t.Gpu) : "—";
        _gpuSpark.Visibility = _chartsHidden ? Visibility.Collapsed : gpuOk ? Visibility.Visible : Visibility.Hidden;
        if (gpuOk) _gpuSpark.SetPoints(history.Recent(120, now, x => x.Gpu));
        _gpuSub.Text = !gpuOk ? (d.Supported ? "not available" : "not on this build")
            : d.Gpu == GpuStatus.Starting ? "starting…"
            : t.GpuOther >= 1 ? $"+ other GPU {Format.Pct(t.GpuOther)}" : "last 2 minutes";

        _ram.Text = Format.Pct(t.RamPct);
        _ramBar.Value = double.IsNaN(t.RamPct) ? 0 : t.RamPct / 100;
        _ramSub.Text = t.RamTotal > 0 ? $"{Format.Bytes(t.RamUsed)} / {Format.Bytes(t.RamTotal)}" : " ";

        if (_wide)
        {
            _disk.Text = Format.Pct(t.Disk);
            _diskSpark.Visibility = _chartsHidden ? Visibility.Collapsed : !double.IsNaN(t.Disk) ? Visibility.Visible : Visibility.Hidden;
            _diskSpark.SetPoints(history.Recent(120, now, x => x.Disk));
            _diskSub.Text = double.IsNaN(t.DiskRead) ? "not available" : $"↓ {Format.Rate(t.DiskRead)}  ↑ {Format.Rate(t.DiskWrite)}";
        }

        // Busiest app by CPU
        _busiest.Inlines.Clear();
        _busiest.Inlines.Add(new Run("Busiest now   ") { Foreground = Palette.B(Palette.Muted) });
        if (!d.Supported) _busiest.Inlines.Add(new Run("per-app details aren't available on this build") { Foreground = Palette.B(Palette.Muted) });
        else if (Top(d, a => a.Cpu) is not AppNow b) _busiest.Inlines.Add(new Run("—") { Foreground = Palette.B(Palette.Muted) });
        else if (b.Cpu < 2) _busiest.Inlines.Add(new Run("nothing much – your PC is idle") { Foreground = Palette.B(Palette.Text) });
        else AppRun(_busiest, b, AppRow.Pct(b.Cpu) + " CPU", Palette.Accent);

        // Last spike
        _lastSpike.Inlines.Clear();
        _lastSpike.Inlines.Add(new Run("Last spike    ") { Foreground = Palette.B(Palette.Muted) });
        if (last == null)
        {
            _lastSpike.Inlines.Add(new Run("No spikes recorded ✓") { Foreground = Palette.B(Palette.Good), FontWeight = FontWeights.SemiBold });
            _lastSpike.ToolTip = null;
        }
        else
        {
            _lastSpike.Inlines.Add(new Run(SpikeText.ShortTime(last.StartUtc) + " · ") { Foreground = Palette.B(Palette.Text) });
            _lastSpike.Inlines.Add(new Run(SpikeText.Culprit(last)) { Foreground = Palette.B(SpikeText.KindColor(last.Kind)), FontWeight = FontWeights.SemiBold });
            _lastSpike.Inlines.Add(new Run(last.Ongoing ? "  · happening now" : "  · " + Format.Ago(PerfClock.ToLocal(last.StartUtc))) { Foreground = Palette.B(Palette.Muted) });
            _lastSpike.ToolTip = SpikeText.Describe(last);
        }

        if (_tall)
        {
            _topGpu.Inlines.Clear();
            _topGpu.Inlines.Add(new Run("Most GPU      ") { Foreground = Palette.B(Palette.Muted) });
            if (d.Gpu == GpuStatus.Available && Top(d, a => a.Gpu) is AppNow g && g.Gpu >= 1) AppRun(_topGpu, g, AppRow.Pct(g.Gpu) + " GPU", Palette.Accent2);
            else _topGpu.Inlines.Add(new Run(d.Gpu == GpuStatus.Available ? "nothing using the GPU" : "—") { Foreground = Palette.B(Palette.Muted) });
            _topRam.Inlines.Clear();
            _topRam.Inlines.Add(new Run("Most memory   ") { Foreground = Palette.B(Palette.Muted) });
            if (Top(d, a => a.Ram) is AppNow m) AppRun(_topRam, m, Format.Bytes(m.Ram), Palette.Rx);
            else _topRam.Inlines.Add(new Run("—") { Foreground = Palette.B(Palette.Muted) });
        }
    }

    private static AppNow? Top(LiveData d, Func<AppNow, double> key)
    {
        AppNow? top = null;
        foreach (var a in d.Apps) { double v = key(a); if (!double.IsNaN(v) && (top == null || v > key(top.Value))) top = a; }
        return top;
    }

    private static void AppRun(TextBlock t, AppNow a, string value, Color c)
    {
        t.Inlines.Add(new Run(a.Name) { FontWeight = FontWeights.SemiBold });
        if (a.Count > 1) t.Inlines.Add(new Run($" ×{a.Count}") { Foreground = Palette.B(Palette.Muted) });
        t.Inlines.Add(new Run("  " + value) { Foreground = Palette.B(c), FontWeight = FontWeights.SemiBold });
    }
}
