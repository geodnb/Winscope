using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinScope.UI;

namespace WinScope.Modules.Performance;

/// <summary>
/// Four stacked time graphs (CPU, GPU, RAM, disk active) on one shared time axis, custom-drawn in the style of
/// AirScope's LiveGraph: wall-clock x-axis that scrolls smoothly between samples, time-aligned per-pixel
/// min/max decimation for long windows (so a 3-second spike still shows in a 2-hour view), spike periods shaded,
/// and a crosshair across all four graphs with a tooltip listing that second's top apps.
/// Redraws are driven by a timer whose rate follows how fast the view actually scrolls (capped at ~60 fps,
/// ~12 fps when the window isn't focused) and which is stopped entirely when the graph isn't on screen or the
/// view is paused on a past moment.
/// </summary>
internal sealed class PerfGraphs : FrameworkElement
{
    public History? History { get; set; }
    public SpikeInfo[] Spikes { get; set; } = Array.Empty<SpikeInfo>();
    public GpuStatus Gpu { get; set; } = GpuStatus.Starting;
    public bool DiskAvailable { get; set; } = true;
    /// <summary>False on a non-64-bit build (the native layouts are x64): GPU/disk show "not available on this build".</summary>
    public bool Supported { get; set; } = true;

    private double _window = 300;
    public double WindowSeconds
    {
        get => _window;
        set { _window = Math.Clamp(value, 30, History.Seconds); Changed(); }
    }

    private DateTime? _viewEnd;
    /// <summary>Right edge of the view (UTC); null = live (following now).</summary>
    public DateTime? ViewEndUtc
    {
        get => _viewEnd;
        set { _viewEnd = value; Changed(); }
    }

    private DateTime? _pinned;
    public DateTime? PinnedUtc
    {
        get => _pinned;
        set { _pinned = value; Changed(); }
    }

    private bool _active;
    /// <summary>Set by the module from the shell's visibility hint.</summary>
    public bool Active
    {
        get => _active;
        set { _active = value; UpdateTimer(); if (value) InvalidateVisual(); }
    }

    /// <summary>A second was clicked (snapped to the nearest recorded sample).</summary>
    public event Action<DateTime>? MomentClicked;
    /// <summary>The user scrolled the view (null = back to live).</summary>
    public event Action<DateTime?>? ViewScrolled;

    private readonly SeriesBuffer _buf = new();
    private readonly DispatcherTimer _timer;
    private bool _loaded;
    private Point? _mouse;
    private Moment? _tipMoment;
    private long _tipTicks = -1;

    // decimated buckets (shared by all four series)
    private readonly long[] _bt = new long[History.Capacity];
    private readonly float[][] _avg = { new float[History.Capacity], new float[History.Capacity], new float[History.Capacity], new float[History.Capacity] };
    private readonly float[][] _min = { new float[History.Capacity], new float[History.Capacity], new float[History.Capacity], new float[History.Capacity] };
    private readonly float[][] _max = { new float[History.Capacity], new float[History.Capacity], new float[History.Capacity], new float[History.Capacity] };

    private static readonly string[] Names = { "CPU", "GPU", "RAM", "Disk active" };
    private static readonly Color[] Colors = { Palette.Accent, Palette.Accent2, Palette.Rx, Palette.Fair };
    private static readonly Pen[] LinePens = Colors.Select(c => Palette.P(c, 1.6)).ToArray();
    private static readonly Brush[] Areas = Colors.Select(MakeArea).ToArray();
    private static readonly Brush[] Envelopes = Colors.Select(c => Palette.B(c, 0.22)).ToArray();
    private static readonly Brush[] Dots = Colors.Select(c => Palette.B(c)).ToArray();
    private static readonly Brush[] Halos = Colors.Select(c => Palette.B(c, 0.25)).ToArray();
    private static readonly Pen GridPen = Palette.P(Palette.Grid, 1);
    private static readonly Pen AxisPen = Palette.P(Palette.Line, 1);
    private static readonly Brush PlotBg = Palette.B(Palette.Bg, 0.45);
    private static readonly Pen CrossPen = Palette.P(Palette.Text, 1, 0.55, DashStyles.Dot);
    private static readonly Pen PinPen = Palette.P(Palette.Accent, 1.5);
    private static readonly Brush PinBg = Palette.B(Palette.Accent);
    private static readonly Brush SpikeBrush = Palette.B(Palette.Bad, 0.16);
    private static readonly Brush BurstBrush = Palette.B(Palette.Weak, 0.18);
    private static readonly Brush LabelBg = Palette.B(Palette.Panel, 0.75);
    private static readonly Brush TipBg = Palette.B(Palette.Panel2, 0.97);
    private static readonly Pen TipPen = Palette.P(Palette.Line, 1);
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    private const double PadL = 34, PadR = 8, PadT = 2, AxisH = 20, Gap = 7;

    private static Brush MakeArea(Color c)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(80, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(8, c.R, c.G, c.B), 1));
        g.Freeze();
        return g;
    }

    public PerfGraphs()
    {
        ClipToBounds = true;
        Focusable = false;
        Cursor = Cursors.Cross;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => InvalidateVisual();
        Loaded += (_, _) => { _loaded = true; UpdateTimer(); };
        Unloaded += (_, _) => { _loaded = false; UpdateTimer(); };
        IsVisibleChanged += (_, _) => { UpdateTimer(); InvalidateVisual(); };
    }

    private void Changed()
    {
        _tipTicks = -1;
        UpdateTimer();
        InvalidateVisual();
    }

    private bool Live => _viewEnd == null;

    private void UpdateTimer()
    {
        bool run = _loaded && _active && IsVisible && Live;
        if (run && !_timer.IsEnabled) _timer.Start();
        else if (!run && _timer.IsEnabled) _timer.Stop();
    }

    /// <summary>New data arrived (called by the module once per second while visible).</summary>
    public void DataChanged()
    {
        _tipTicks = -1;
        if (!Live || !_timer.IsEnabled) InvalidateVisual();
    }

    // ================================================================== input

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; InvalidateVisual(); }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var p = e.GetPosition(this);
        if (SnapAt(p) is DateTime t)
        {
            try { MomentClicked?.Invoke(t); } catch (Exception ex) { App.LogError(ex); }
            e.Handled = true;
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        // Wheel up = back in time, wheel down = towards now (and back to live once "now" is reached).
        var now = PerfClock.Now;
        var end = (_viewEnd ?? now).AddSeconds(-Math.Sign(e.Delta) * _window * 0.15);
        var oldest = History?.OldestUtc ?? now;
        var minEnd = oldest.AddSeconds(Math.Min(_window, (now - oldest).TotalSeconds) * 0.5);
        if (end < minEnd) end = minEnd;
        DateTime? next = end >= now.AddSeconds(-0.5) ? null : end;
        _viewEnd = next;
        Changed();
        try { ViewScrolled?.Invoke(next); } catch (Exception ex) { App.LogError(ex); }
        e.Handled = true;
    }

    // ================================================================== layout helpers

    private Rect PaneRect(int i, double w, double h)
    {
        double paneH = Math.Max(10, (h - PadT - AxisH - 3 * Gap) / 4);
        return new Rect(PadL, PadT + i * (paneH + Gap), Math.Max(1, w - PadL - PadR), paneH);
    }

    private (DateTime start, DateTime end) ViewRange()
    {
        var end = _viewEnd ?? PerfClock.Now;
        return (end.AddSeconds(-_window), end);
    }

    /// <summary>The recorded sample nearest the mouse x, if the point is over a plot.</summary>
    private DateTime? SnapAt(Point p)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 80 || History == null) return null;
        var r0 = PaneRect(0, w, h);
        var r3 = PaneRect(3, w, h);
        if (p.X < r0.Left || p.X > r0.Right || p.Y < r0.Top || p.Y > r3.Bottom) return null;
        var (start, _) = ViewRange();
        var t = start.AddSeconds((p.X - r0.Left) / r0.Width * _window);
        int i = Nearest(t.Ticks);
        if (i < 0) return null;
        double tol = Math.Max(2.5, _window / r0.Width * 3);
        var ts = new DateTime(_buf.T[i], DateTimeKind.Utc);
        return Math.Abs((ts - t).TotalSeconds) <= tol ? ts : null;
    }

    private int Nearest(long ticks)
    {
        int n = _buf.Count;
        if (n == 0) return -1;
        int lo = 0, hi = n - 1;
        while (lo < hi) { int mid = (lo + hi) >>> 1; if (_buf.T[mid] < ticks) lo = mid + 1; else hi = mid; }
        if (lo > 0 && ticks - _buf.T[lo - 1] < _buf.T[lo] - ticks) lo--;
        return lo;
    }

    // ================================================================== render

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable everywhere
        if (w < 80 || h < 80 || History == null) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;

        var (start, end) = ViewRange();
        History.CopySeries(start.AddSeconds(-3), end.AddSeconds(1), _buf);
        var r0 = PaneRect(0, w, h);
        double secPerPx = _window / r0.Width;
        double X(long ticks) => r0.Left + (ticks - start.Ticks) / (double)TimeSpan.TicksPerSecond / _window * r0.Width;

        int nb = Decimate(secPerPx, out bool decimated);
        double maxGap = Math.Max(3.5, secPerPx * 3) * TimeSpan.TicksPerSecond;

        // hover snap (raw samples)
        int hi = -1;
        int hoverPane = -1;
        if (_mouse is Point m && m.X >= r0.Left && m.X <= r0.Right)
        {
            for (int i = 0; i < 4; i++) { var r = PaneRect(i, w, h); if (m.Y >= r.Top - Gap / 2 && m.Y <= r.Bottom + Gap / 2) hoverPane = i; }
            if (hoverPane >= 0)
            {
                long mt = start.Ticks + (long)((m.X - r0.Left) / r0.Width * _window * TimeSpan.TicksPerSecond);
                hi = Nearest(mt);
                if (hi >= 0 && Math.Abs(_buf.T[hi] - mt) > Math.Max(2.5, secPerPx * 3) * TimeSpan.TicksPerSecond) hi = -1;
            }
        }

        for (int pane = 0; pane < 4; pane++)
        {
            var r = PaneRect(pane, w, h);
            dc.DrawRectangle(PlotBg, null, r);
            double Y(double v) => r.Bottom - Math.Clamp(v, 0, 100) / 100 * r.Height;
            double ymid = Y(50);
            dc.DrawLine(GridPen, new Point(r.Left, ymid), new Point(r.Right, ymid));
            if (r.Height >= 34)
            {
                var l50 = Palette.Text_("50", 9.5, Palette.Muted, dpi, alpha: 0.8);
                dc.DrawText(l50, new Point(r.Left - l50.Width - 5, ymid - l50.Height / 2));
                var l100 = Palette.Text_("100", 9.5, Palette.Muted, dpi, alpha: 0.8);
                dc.DrawText(l100, new Point(r.Left - l100.Width - 5, r.Top - 1));
            }

            dc.PushClip(new RectangleGeometry(r));
            DrawSpikes(dc, pane, r, X);
            DrawGrid(dc, r, start, end, secPerPx, dpi, labels: false);

            bool unavailable = (pane == 1 && Gpu == GpuStatus.Unavailable) || (pane == 3 && !DiskAvailable && _buf.Count > 5 && AllNaN(_buf.Disk));
            if (unavailable)
            {
                string msg = !Supported ? "Not available on this build (needs the 64-bit WinScope)."
                    : pane == 1 ? "GPU usage isn't available on this PC (it needs a WDDM 2 graphics driver)." : "Disk activity counters aren't available on this PC.";
                var ft = Palette.Text_(msg, 11.5, Palette.Muted, dpi);
                dc.DrawText(ft, new Point(r.Left + (r.Width - ft.Width) / 2, r.Top + (r.Height - ft.Height) / 2));
            }
            else if (nb >= 1)
            {
                DrawSeries(dc, pane, r, nb, decimated, maxGap, X, Y);
                // head dot while live
                if (Live && _buf.Count > 0)
                {
                    float lv = Series(pane)[_buf.Count - 1];
                    if (!float.IsNaN(lv) && (PerfClock.Now.Ticks - _buf.T[_buf.Count - 1]) < 3 * TimeSpan.TicksPerSecond)
                    {
                        var hp = new Point(X(_buf.T[_buf.Count - 1]), Y(lv));
                        dc.DrawEllipse(Halos[pane], null, hp, 6, 6);
                        dc.DrawEllipse(Dots[pane], null, hp, 3, 3);
                    }
                }
            }
            dc.Pop();
            dc.DrawRectangle(null, AxisPen, r);

            // value label
            string val;
            int vi = hi >= 0 ? hi : LastValid(Series(pane));
            float v = vi >= 0 ? Series(pane)[vi] : float.NaN;
            val = unavailable ? "not available" : float.IsNaN(v) ? "—" : Format.Pct(v);
            var name = Palette.Text_(Names[pane], 11, Palette.Muted, dpi, true);
            var vt = Palette.Text_(val, 11, Colors[pane], dpi, true);
            var lr = new Rect(r.Left + 4, r.Top + 3, name.Width + vt.Width + 16, Math.Max(name.Height, vt.Height) + 2);
            dc.DrawRoundedRectangle(LabelBg, null, lr, 4, 4);
            dc.DrawText(name, new Point(lr.Left + 5, lr.Top + 1));
            dc.DrawText(vt, new Point(lr.Left + 11 + name.Width, lr.Top + 1));
        }

        // time axis labels under the last pane
        var r3 = PaneRect(3, w, h);
        DrawGrid(dc, new Rect(r3.Left, r3.Bottom, r3.Width, AxisH), start, end, secPerPx, dpi, labels: true);
        var top = PaneRect(0, w, h).Top;

        if (_buf.Count < 2)
        {
            var ft = Palette.Text_("Collecting… the graphs fill in as WinScope records each second.", 12, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(r0.Left + (r0.Width - ft.Width) / 2, PaneRect(1, w, h).Bottom + Gap / 2 - ft.Height / 2));
        }

        // pinned moment
        if (_pinned is DateTime pin && pin >= start && pin <= end)
        {
            double x = X(pin.Ticks);
            dc.DrawLine(PinPen, new Point(x, top), new Point(x, r3.Bottom));
            var ft = Palette.Text_(PerfClock.ToLocal(pin).ToString("HH:mm:ss", Inv), 10, Palette.C("#06201D"), dpi, true);
            var lr = new Rect(Math.Clamp(x - ft.Width / 2 - 4, r3.Left, r3.Right - ft.Width - 8), r3.Bottom + 2, ft.Width + 8, ft.Height + 2);
            dc.DrawRoundedRectangle(PinBg, null, lr, 4, 4);
            dc.DrawText(ft, new Point(lr.Left + 4, lr.Top + 1));
        }

        // crosshair + tooltip
        if (hi >= 0)
        {
            double x = X(_buf.T[hi]);
            dc.DrawLine(CrossPen, new Point(x, top), new Point(x, r3.Bottom));
            for (int pane = 0; pane < 4; pane++)
            {
                float v = Series(pane)[hi];
                if (float.IsNaN(v)) continue;
                var r = PaneRect(pane, w, h);
                dc.DrawEllipse(Dots[pane], null, new Point(x, r.Bottom - Math.Clamp(v, 0, 100) / 100 * r.Height), 3, 3);
            }
            if (_tipTicks != _buf.T[hi])
            {
                _tipTicks = _buf.T[hi];
                _tipMoment = History.GetMoment(new DateTime(_tipTicks, DateTimeKind.Utc), TimeSpan.FromSeconds(0.5));
            }
            if (_tipMoment != null && _mouse is Point mp)
                DrawTip(dc, hoverPane, _tipMoment, mp, new Rect(0, 0, w, h), dpi);
        }

        if (Live)
        {
            // Redraw often enough that the view moves in ~¼-pixel steps; ≥16 ms (≈60 fps), ≥80 ms when not focused.
            var win = Window.GetWindow(this);
            double minMs = win?.IsActive == false ? 80 : 16;
            double ms = Math.Clamp(secPerPx * 1000 / 4, minMs, 1000);
            var iv = TimeSpan.FromMilliseconds(ms);
            if (Math.Abs((_timer.Interval - iv).TotalMilliseconds) > 2) _timer.Interval = iv;
        }
    }

    private float[] Series(int pane) => pane switch { 0 => _buf.Cpu, 1 => _buf.Gpu, 2 => _buf.Ram, _ => _buf.Disk };

    private int LastValid(float[] s)
    {
        for (int i = _buf.Count - 1; i >= 0 && i >= _buf.Count - 5; i--) if (!float.IsNaN(s[i])) return i;
        return -1;
    }

    private bool AllNaN(float[] s)
    {
        for (int i = 0; i < _buf.Count; i++) if (!float.IsNaN(s[i])) return false;
        return true;
    }

    /// <summary>
    /// Bucket the copied samples on an absolute time grid (~1 bucket per pixel) so buckets don't shift as the
    /// view scrolls (no shimmer). Without decimation each sample is its own bucket.
    /// </summary>
    private int Decimate(double secPerPx, out bool decimated)
    {
        int n = _buf.Count;
        decimated = secPerPx > 1.3;
        if (!decimated)
        {
            for (int i = 0; i < n; i++)
            {
                _bt[i] = _buf.T[i];
                for (int s = 0; s < 4; s++) { float v = Series(s)[i]; _avg[s][i] = v; _min[s][i] = v; _max[s][i] = v; }
            }
            return n;
        }
        long bucket = Math.Max(1, (long)(secPerPx * TimeSpan.TicksPerSecond));
        int nb = 0, k = 0;
        Span<double> sum = stackalloc double[4];
        Span<int> cnt = stackalloc int[4];
        Span<float> mn = stackalloc float[4];
        Span<float> mx = stackalloc float[4];
        while (k < n)
        {
            long id = _buf.T[k] / bucket;
            sum.Clear(); cnt.Clear(); mn.Fill(float.MaxValue); mx.Fill(float.MinValue);
            while (k < n && _buf.T[k] / bucket == id)
            {
                for (int s = 0; s < 4; s++)
                {
                    float v = Series(s)[k];
                    if (float.IsNaN(v)) continue;
                    sum[s] += v; cnt[s]++;
                    if (v < mn[s]) mn[s] = v;
                    if (v > mx[s]) mx[s] = v;
                }
                k++;
            }
            _bt[nb] = id * bucket + bucket / 2;
            for (int s = 0; s < 4; s++)
            {
                if (cnt[s] == 0) { _avg[s][nb] = _min[s][nb] = _max[s][nb] = float.NaN; continue; }
                _avg[s][nb] = (float)(sum[s] / cnt[s]); _min[s][nb] = mn[s]; _max[s][nb] = mx[s];
            }
            nb++;
        }
        return nb;
    }

    private void DrawSeries(DrawingContext dc, int s, Rect r, int nb, bool decimated, double maxGapTicks,
        Func<long, double> X, Func<double, double> Y)
    {
        var avg = _avg[s];
        if (decimated)
        {
            var env = new StreamGeometry();
            using (var g = env.Open())
            {
                int segStart = -1;
                for (int i = 0; i <= nb; i++)
                {
                    bool brk = i == nb || float.IsNaN(avg[i]) || (segStart >= 0 && _bt[i] - _bt[i - 1] > maxGapTicks);
                    if (brk && segStart >= 0)
                    {
                        if (i - segStart >= 1)
                        {
                            g.BeginFigure(new Point(X(_bt[segStart]), Y(_max[s][segStart])), true, true);
                            for (int k = segStart + 1; k < i; k++) g.LineTo(new Point(X(_bt[k]), Y(_max[s][k])), false, false);
                            for (int k = i - 1; k >= segStart; k--) g.LineTo(new Point(X(_bt[k]), Y(_min[s][k])), false, false);
                        }
                        segStart = -1;
                    }
                    if (i < nb && !float.IsNaN(avg[i]) && segStart < 0) segStart = i;
                }
            }
            env.Freeze();
            dc.DrawGeometry(Envelopes[s], null, env);
        }

        var area = new StreamGeometry();
        var line = new StreamGeometry();
        using (var ga = area.Open())
        using (var gl = line.Open())
        {
            bool open = false;
            Point prev = default;
            long prevT = 0;
            for (int i = 0; i < nb; i++)
            {
                float v = avg[i];
                if (float.IsNaN(v))
                {
                    if (open) { ga.LineTo(new Point(prev.X, r.Bottom), false, false); open = false; }
                    continue;
                }
                var p = new Point(X(_bt[i]), Y(v));
                if (open && _bt[i] - prevT > maxGapTicks)
                {
                    ga.LineTo(new Point(prev.X, r.Bottom), false, false);
                    open = false;
                }
                if (!open)
                {
                    ga.BeginFigure(new Point(p.X, r.Bottom), true, true);
                    ga.LineTo(p, false, false);
                    gl.BeginFigure(p, false, false);
                    open = true;
                }
                else { ga.LineTo(p, false, false); gl.LineTo(p, true, true); }
                prev = p; prevT = _bt[i];
            }
            if (open) ga.LineTo(new Point(prev.X, r.Bottom), false, false);
        }
        area.Freeze();
        line.Freeze();
        dc.DrawGeometry(Areas[s], null, area);
        dc.DrawGeometry(null, LinePens[s], line);
    }

    private void DrawSpikes(DrawingContext dc, int pane, Rect r, Func<long, double> X)
    {
        foreach (var sp in Spikes)
        {
            bool mine = pane switch
            {
                0 => sp.Kind is SpikeKind.Cpu or SpikeKind.Burst,
                2 => sp.Kind == SpikeKind.Memory,
                3 => sp.Kind == SpikeKind.Disk,
                _ => false,
            };
            if (!mine) continue;
            double x0 = X(sp.StartUtc.Ticks - TimeSpan.TicksPerSecond / 2);
            double x1 = X(sp.EndUtc.Ticks + TimeSpan.TicksPerSecond / 2);
            if (x1 < r.Left || x0 > r.Right) continue;
            if (x1 - x0 < 2) { double c = (x0 + x1) / 2; x0 = c - 1; x1 = c + 1; }
            dc.DrawRectangle(sp.Kind == SpikeKind.Burst ? BurstBrush : SpikeBrush, null, new Rect(x0, r.Top, x1 - x0, r.Height));
        }
    }

    private readonly List<(long utc, long local)> _ticks = new();

    /// <summary>Vertical grid lines at round local clock times (and their labels on the axis strip).</summary>
    private void DrawGrid(DrawingContext dc, Rect r, DateTime start, DateTime end, double secPerPx, double dpi, bool labels)
    {
        double[] steps = { 30, 60, 120, 300, 600, 900, 1200, 1800, 3600 };
        double step = steps[^1];
        foreach (var s in steps) if (s / secPerPx >= 75) { step = s; break; }
        // Ticks are found in wall-clock time (each converted to local time individually – DST-safe), then mapped
        // back onto the timeline.
        PerfClock.AxisTicks(PerfClock.ToWall(start), PerfClock.ToWall(end), (long)(step * TimeSpan.TicksPerSecond), TimeZoneInfo.Local, _ticks);
        foreach (var (wallUtc, local) in _ticks)
        {
            long stamp = PerfClock.FromWall(new DateTime(wallUtc, DateTimeKind.Utc)).Ticks;
            double x = r.Left + (stamp - start.Ticks) / (double)TimeSpan.TicksPerSecond / _window * r.Width;
            if (x < r.Left + 1 || x > r.Right - 1) continue;
            if (!labels) { dc.DrawLine(GridPen, new Point(x, r.Top), new Point(x, r.Bottom)); continue; }
            var ft = Palette.Text_(new DateTime(local).ToString(step < 60 ? "HH:mm:ss" : "HH:mm", Inv), 10, Palette.Muted, dpi);
            double lx = x - ft.Width / 2;
            if (lx < r.Left || lx + ft.Width > r.Right - (Live ? 26 : 0)) continue;
            dc.DrawText(ft, new Point(lx, r.Top + 4));
        }
        if (labels && Live)
        {
            var ft = Palette.Text_("now", 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(r.Right - ft.Width, r.Top + 4));
        }
    }

    // ================================================================== tooltip

    private void DrawTip(DrawingContext dc, int pane, Moment m, Point anchor, Rect bounds, double dpi)
    {
        var t = m.Totals;
        string time = PerfClock.ToLocal(m.Utc).ToString("HH:mm:ss", Inv);
        string title;
        AppValue[] apps;
        Func<double, string> fmt;
        string? extra = null;
        switch (pane)
        {
            case 1:
                title = $"{time} · GPU {Format.Pct(t.Gpu)}";
                // The main (discrete) adapter is reported; mention any other busy adapter, e.g. integrated graphics.
                if (t.GpuOther >= 1) extra = $"(+ other GPU {Format.Pct(t.GpuOther)}, e.g. integrated graphics)";
                apps = m.TopGpu; fmt = v => Format.Pct(v);
                break;
            case 2:
                title = $"{time} · RAM {Format.Pct(t.RamPct)}";
                if (t.RamTotal > 0) extra = $"{Format.Bytes(t.RamUsed)} of {Format.Bytes(t.RamTotal)} in use";
                apps = m.TopRam; fmt = v => Format.Bytes(v);
                break;
            case 3:
                title = $"{time} · Disk {Format.Pct(t.Disk)} busy";
                if (!double.IsNaN(t.DiskRead)) extra = $"Reading {Format.Rate(t.DiskRead)} · writing {Format.Rate(t.DiskWrite)}";
                apps = m.TopIo; fmt = v => Format.Rate(v);
                break;
            default:
                title = $"{time} · CPU {Format.Pct(t.Cpu)}";
                apps = m.TopCpu; fmt = v => Format.Pct(v, v < 10 ? 1 : 0);
                break;
        }
        string head = pane switch { 1 => "Top GPU users", 2 => "Top memory users", 3 => "Top I/O (disk + network + other)", _ => "Top CPU users" };
        string? spike = null;
        foreach (var sp in Spikes)
        {
            if (m.Utc < sp.StartUtc.AddSeconds(-0.5) || m.Utc > sp.EndUtc.AddSeconds(0.5)) continue;
            spike = "⚠ " + SpikeText.Describe(sp);
            break;
        }

        var ftTitle = Palette.Text_(title, 12, Palette.Text, dpi, true);
        var ftExtra = extra != null ? Palette.Text_(extra, 11, Palette.Muted, dpi) : null;
        var ftHead = Palette.Text_(head, 10.5, Palette.Muted, dpi, true);
        int n = Math.Min(apps.Length, 5);
        var names = new FormattedText[n];
        var vals = new FormattedText[n];
        double nameW = 0, valW = 0;
        for (int i = 0; i < n; i++)
        {
            names[i] = Palette.Text_(Trim(apps[i].Name, 30), 11.5, Palette.Text, dpi);
            vals[i] = Palette.Text_(fmt(apps[i].Value), 11.5, Colors[pane], dpi, true);
            nameW = Math.Max(nameW, names[i].Width);
            valW = Math.Max(valW, vals[i].Width);
        }
        var ftNone = n == 0 ? Palette.Text_(pane == 1 && Gpu != GpuStatus.Available ? "GPU details aren't available" : "Nothing notable this second", 11.5, Palette.Muted, dpi) : null;
        var ftSpike = spike != null ? Palette.Text_(spike, 11, Palette.Bad, dpi) : null;

        const double pad = 9, rowH = 17;
        double width = Math.Max(ftTitle.Width, Math.Max(ftHead.Width, nameW + 18 + valW));
        if (ftExtra != null) width = Math.Max(width, ftExtra.Width);
        if (ftNone != null) width = Math.Max(width, ftNone.Width);
        if (ftSpike != null) width = Math.Max(width, ftSpike.Width);
        double height = ftTitle.Height + (ftExtra != null ? ftExtra.Height + 1 : 0) + 6 + ftHead.Height + 2
                        + (n > 0 ? n * rowH : rowH) + (ftSpike != null ? ftSpike.Height + 6 : 0);
        var size = new Size(width + pad * 2, height + pad * 2 - 2);
        double x = anchor.X + 16, y = anchor.Y - size.Height / 2;
        if (x + size.Width > bounds.Right) x = anchor.X - size.Width - 16;
        x = Math.Max(bounds.Left, x);
        y = Math.Clamp(y, bounds.Top, Math.Max(bounds.Top, bounds.Bottom - size.Height));
        var rect = new Rect(x, y, size.Width, size.Height);
        dc.DrawRoundedRectangle(TipBg, TipPen, rect, 7, 7);
        double cy = rect.Top + pad - 1, cx = rect.Left + pad;
        dc.DrawText(ftTitle, new Point(cx, cy)); cy += ftTitle.Height;
        if (ftExtra != null) { dc.DrawText(ftExtra, new Point(cx, cy + 1)); cy += ftExtra.Height + 1; }
        cy += 6;
        dc.DrawText(ftHead, new Point(cx, cy)); cy += ftHead.Height + 2;
        if (n == 0) { dc.DrawText(ftNone!, new Point(cx, cy)); cy += rowH; }
        for (int i = 0; i < n; i++)
        {
            dc.DrawText(names[i], new Point(cx, cy));
            dc.DrawText(vals[i], new Point(rect.Right - pad - vals[i].Width, cy));
            cy += rowH;
        }
        if (ftSpike != null) dc.DrawText(ftSpike, new Point(cx, cy + 6));
    }

    internal static string Trim(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
