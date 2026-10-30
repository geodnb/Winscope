using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinScope.UI;

namespace WinScope.Modules.Network;

/// <summary>
/// Total download/upload over the last 5–30 minutes (adapted from AirScope's LiveGraph). The x-axis is wall-clock
/// time and a DispatcherTimer re-renders it about every half pixel of scroll (5 min ≈ every 0.2 s, 30 min ≈ every
/// 1 s; slower again when the window isn't focused). The timer only runs while the module says the view is on
/// screen (<see cref="Active"/>) and the control is loaded and visible – no per-frame render hook. Long windows are bucketed on an absolute time grid (~1 bucket per
/// pixel) so the line doesn't shimmer as it moves. Hover shows the exact values of the nearest second.
/// </summary>
internal sealed class NetGraph : FrameworkElement
{
    public RateHistory? History { get; set; }
    public double WindowSeconds { get; set; } = 300;

    public static readonly Color DownColor = Palette.Accent;
    public static readonly Color UpColor = Palette.Rx;

    private readonly List<RateSample> _work = new();
    private readonly List<Bucket> _pts = new();
    private readonly DispatcherTimer _timer;
    private Point? _mouse;
    private double _yMax = 64 * 1024;
    private bool _loaded, _active;
    private bool _easing;
    private Window? _host;

    private static readonly Pen GridPen = Palette.P(Palette.Grid, 1);
    private static readonly Pen AxisPen = Palette.P(Palette.Line, 1);
    private static readonly Pen DownPen = Palette.P(DownColor, 1.8);
    private static readonly Pen UpPen = Palette.P(UpColor, 1.5, 0.95);
    private static readonly Pen CrossPen = Palette.P(Palette.Muted, 1, 0.6, DashStyles.Dot);
    private static readonly Brush DownArea = Gradient(DownColor, 80);
    private static readonly Brush UpArea = Gradient(UpColor, 40);
    private static readonly Brush TipBg = Palette.B(Palette.Panel2, 0.96);
    private static readonly Pen TipPen = Palette.P(Palette.Line, 1);
    private static readonly Brush DownDot = Palette.B(DownColor);
    private static readonly Brush UpDot = Palette.B(UpColor);

    private readonly record struct Bucket(DateTime T, double Rx, double Tx);

    private static Brush Gradient(Color c, byte top)
    {
        var g = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        g.GradientStops.Add(new GradientStop(Color.FromArgb(top, c.R, c.G, c.B), 0));
        g.GradientStops.Add(new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1));
        g.Freeze();
        return g;
    }

    public NetGraph()
    {
        ClipToBounds = true;
        _timer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { InvalidateVisual(); RetuneTimer(); };
        Loaded += (_, _) =>
        {
            _loaded = true;
            var host = Window.GetWindow(this);
            if (host != _host)
            {
                if (_host != null) { _host.Activated -= OnHostActivation; _host.Deactivated -= OnHostActivation; }
                _host = host;
                if (_host != null) { _host.Activated += OnHostActivation; _host.Deactivated += OnHostActivation; }
            }
            UpdateTimer();
        };
        Unloaded += (_, _) => { _loaded = false; UpdateTimer(); };
        IsVisibleChanged += (_, _) => { UpdateTimer(); InvalidateVisual(); };
        SizeChanged += (_, _) => RetuneTimer();
    }

    /// <summary>Set by the module from SetVisibility (false when hidden, on another tool, or minimised).</summary>
    public bool Active
    {
        get => _active;
        set { if (_active == value) return; _active = value; UpdateTimer(); if (value) InvalidateVisual(); }
    }

    public void MarkDirty() { RetuneTimer(); InvalidateVisual(); }

    private void OnHostActivation(object? sender, EventArgs e) => RetuneTimer();

    private void UpdateTimer()
    {
        bool run = _loaded && _active && IsVisible && _host?.WindowState != WindowState.Minimized;
        if (run && !_timer.IsEnabled) { RetuneTimer(); _timer.Start(); }
        else if (!run && _timer.IsEnabled) _timer.Stop();
    }

    /// <summary>About half a pixel of scroll per redraw: ≥ 200 ms, ≤ 2 s; doubled when WinScope isn't the focused window,
    /// and quick (200 ms) while the y-axis is easing to a new scale.</summary>
    private void RetuneTimer()
    {
        double plotW = Math.Max(50, ActualWidth - 72);
        double ms = WindowSeconds / plotW / 2 * 1000;
        if (_host != null && !_host.IsActive) ms *= 2;
        if (_easing) ms = 200;
        var iv = TimeSpan.FromMilliseconds(Math.Clamp(ms, 200, 2000));
        if (Math.Abs((_timer.Interval - iv).TotalMilliseconds) > 5) _timer.Interval = iv;
    }

    protected override void OnMouseMove(MouseEventArgs e) { _mouse = e.GetPosition(this); InvalidateVisual(); }
    protected override void OnMouseLeave(MouseEventArgs e) { _mouse = null; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        if (w < 80 || h < 50) return;
        double dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));   // hit-testable for hover

        const double padL = 62, padR = 10, padT = 8, padB = 20;
        var plot = new Rect(padL, padT, Math.Max(1, w - padL - padR), Math.Max(1, h - padT - padB));
        var now = MonoClock.UtcNow;   // same clock as the history
        var start = now.AddSeconds(-WindowSeconds);
        History?.CopySince(start.AddSeconds(-2), _work);

        Decimate(_work, WindowSeconds / plot.Width, _pts);
        double mx = 0;
        foreach (var b in _pts) mx = Math.Max(mx, Math.Max(b.Rx, b.Tx));
        double target = NiceCeil(Math.Max(16 * 1024, mx * 1.12));
        // Ease axis changes (grow fast so peaks never clip for long, shrink slowly).
        _yMax += (target - _yMax) * (target > _yMax ? 0.5 : 0.12);
        bool easing = Math.Abs(_yMax - target) / target >= 0.01;
        if (!easing) _yMax = target;
        if (easing != _easing) { _easing = easing; RetuneTimer(); }
        double yMax = _yMax;
        double Y(double v) => plot.Bottom - Math.Min(v, yMax * 1.2) / yMax * plot.Height;
        double X(DateTime t) => plot.Left + (t - start).TotalSeconds / WindowSeconds * plot.Width;

        // grid + y labels
        for (int i = 0; i <= 4; i++)
        {
            double v = yMax * i / 4, y = plot.Bottom - i / 4.0 * plot.Height;
            if (i > 0) dc.DrawLine(GridPen, new Point(plot.Left, y), new Point(plot.Right, y));
            var ft = Palette.Text_(AxisRate(v), 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left - ft.Width - 6, y - ft.Height / 2));
        }
        double step = WindowSeconds <= 300 ? 60 : WindowSeconds <= 900 ? 180 : 300;
        for (double sec = 0; sec <= WindowSeconds + 0.1; sec += step)
        {
            double x = plot.Right - sec / WindowSeconds * plot.Width;
            if (sec > 0) dc.DrawLine(GridPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
            string lab = sec == 0 ? "now" : $"-{sec / 60:0} min";
            var ft = Palette.Text_(lab, 10, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(Math.Clamp(x - ft.Width / 2, plot.Left, plot.Right - ft.Width), plot.Bottom + 4));
        }
        dc.DrawLine(AxisPen, new Point(plot.Left, plot.Bottom), new Point(plot.Right, plot.Bottom));

        if (_pts.Count < 2)
        {
            var ft = Palette.Text_("Measuring…", 12.5, Palette.Muted, dpi);
            dc.DrawText(ft, new Point(plot.Left + (plot.Width - ft.Width) / 2, plot.Top + (plot.Height - ft.Height) / 2));
            return;
        }

        double maxGap = Math.Max(4, WindowSeconds / plot.Width * 4);   // break the line on real gaps (sleep)
        dc.PushClip(new RectangleGeometry(plot));
        DrawSeries(dc, b => b.Tx, UpPen, UpArea, X, Y, plot, maxGap);
        DrawSeries(dc, b => b.Rx, DownPen, DownArea, X, Y, plot, maxGap);
        dc.Pop();

        // Hover crosshair + readout (raw 1 s samples, not buckets)
        if (_mouse is Point m && plot.Contains(m) && _work.Count > 0)
        {
            var mt = start.AddSeconds((m.X - plot.Left) / plot.Width * WindowSeconds);
            int i = NearestIndex(_work, mt.Ticks);
            if (i >= 0 && Math.Abs((_work[i].Utc - mt).TotalSeconds) < Math.Max(3, WindowSeconds / plot.Width * 3))
            {
                var s = _work[i];
                double x = X(s.Utc);
                dc.DrawLine(CrossPen, new Point(x, plot.Top), new Point(x, plot.Bottom));
                dc.DrawEllipse(DownDot, null, new Point(x, Y(s.Rx)), 3, 3);
                dc.DrawEllipse(UpDot, null, new Point(x, Y(s.Tx)), 3, 3);
                string txt = $"{s.Utc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture)}\n↓ {Format.Rate(s.Rx)}\n↑ {Format.Rate(s.Tx)}";
                DrawTip(dc, txt, new Point(x, Math.Min(Y(s.Rx), Y(s.Tx))), plot, dpi);
            }
        }
    }

    private void DrawSeries(DrawingContext dc, Func<Bucket, double> val, Pen pen, Brush area, Func<DateTime, double> X,
        Func<double, double> Y, Rect plot, double maxGap)
    {
        var line = new StreamGeometry();
        var fill = new StreamGeometry();
        using (var gl = line.Open())
        using (var ga = fill.Open())
        {
            bool open = false; Point prev = default; DateTime prevT = default;
            foreach (var b in _pts)
            {
                var p = new Point(X(b.T), Y(val(b)));
                if (open && (b.T - prevT).TotalSeconds > maxGap)
                {
                    ga.LineTo(new Point(prev.X, plot.Bottom), false, false);
                    open = false;
                }
                if (!open)
                {
                    gl.BeginFigure(p, false, false);
                    ga.BeginFigure(new Point(p.X, plot.Bottom), true, true);
                    ga.LineTo(p, false, false);
                    open = true;
                }
                else { gl.LineTo(p, true, true); ga.LineTo(p, false, false); }
                prev = p; prevT = b.T;
            }
            if (open) ga.LineTo(new Point(prev.X, plot.Bottom), false, false);
        }
        line.Freeze(); fill.Freeze();
        dc.DrawGeometry(area, null, fill);
        dc.DrawGeometry(null, pen, line);
    }

    /// <summary>~1 bucket per pixel on an absolute time grid, so buckets don't shift as the view scrolls.</summary>
    private static void Decimate(List<RateSample> src, double secPerPx, List<Bucket> res)
    {
        res.Clear();
        if (src.Count == 0) return;
        if (secPerPx <= 1.2)
        {
            foreach (var s in src) res.Add(new Bucket(s.Utc, s.Rx, s.Tx));
            return;
        }
        long bucketTicks = Math.Max(1, (long)(secPerPx * TimeSpan.TicksPerSecond));
        int i = 0;
        while (i < src.Count)
        {
            long id = src[i].Ticks / bucketTicks;
            double rx = 0, tx = 0; int n = 0;
            while (i < src.Count && src[i].Ticks / bucketTicks == id) { rx += src[i].Rx; tx += src[i].Tx; n++; i++; }
            res.Add(new Bucket(new DateTime(id * bucketTicks + bucketTicks / 2, DateTimeKind.Utc), rx / n, tx / n));
        }
    }

    private static int NearestIndex(List<RateSample> s, long t)
    {
        int lo = 0, hi = s.Count - 1;
        while (lo < hi) { int mid = (lo + hi) / 2; if (s[mid].Ticks < t) lo = mid + 1; else hi = mid; }
        if (lo > 0 && t - s[lo - 1].Ticks < s[lo].Ticks - t) lo--;
        return lo;
    }

    private static void DrawTip(DrawingContext dc, string text, Point anchor, Rect bounds, double dpi)
    {
        var ft = Palette.Text_(text, 11.5, Palette.Text, dpi);
        var size = new Size(ft.Width + 16, ft.Height + 10);
        double x = anchor.X + 12, y = anchor.Y - size.Height - 10;
        if (x + size.Width > bounds.Right) x = anchor.X - size.Width - 12;
        x = Math.Max(bounds.Left, x);
        if (y < bounds.Top) y = anchor.Y + 12;
        if (y + size.Height > bounds.Bottom) y = bounds.Bottom - size.Height;
        y = Math.Max(bounds.Top, y);
        var r = new Rect(x, y, size.Width, size.Height);
        dc.DrawRoundedRectangle(TipBg, TipPen, r, 6, 6);
        dc.DrawText(ft, new Point(r.X + 8, r.Y + 5));
    }

    /// <summary>Round up to 1/2/2.5/5 × a power of 1024-scaled unit, so axis labels read "2 MB/s", "500 KB/s".</summary>
    internal static double NiceCeil(double bytes)
    {
        if (bytes <= 1) return 1;
        double unit = 1;
        while (bytes / unit >= 1000 && unit < Math.Pow(1024, 4)) unit *= 1024;
        double v = bytes / unit;
        double mag = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double n = v / mag;
        double nice = n <= 1 ? 1 : n <= 2 ? 2 : n <= 2.5 ? 2.5 : n <= 5 ? 5 : 10;
        return nice * mag * unit;
    }

    internal static string AxisRate(double bytes)
    {
        if (bytes < 1) return "0";
        string[] u = { "B/s", "KB/s", "MB/s", "GB/s", "TB/s" };
        int i = 0;
        while (bytes >= 1000 && i < u.Length - 1) { bytes /= 1024; i++; }
        return bytes.ToString(bytes >= 10 ? "0" : "0.#", CultureInfo.InvariantCulture) + " " + u[i];
    }
}
