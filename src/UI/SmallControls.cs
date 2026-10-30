using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WinScope.Core;

namespace WinScope.UI;

/// <summary>Tiny filled line chart for dashboard cards. Points are evenly spaced; NaN breaks the line.</summary>
public sealed class Sparkline : FrameworkElement
{
    private IReadOnlyList<double> _pts = Array.Empty<double>();
    /// <summary>Fixed y-range. Max grows to fit the data unless <see cref="FixedMax"/> is set.</summary>
    public double Min { get; set; }
    public double Max { get; set; } = 100;
    public bool FixedMax { get; set; } = true;
    public Color Stroke { get; set; } = Palette.Accent;

    public void SetPoints(IReadOnlyList<double> pts) { _pts = pts; InvalidateVisual(); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (_pts.Count < 2 || w < 4 || h < 4) return;
        double lo = Min, hi = Max;
        if (!FixedMax) foreach (var p in _pts) if (!double.IsNaN(p) && p > hi) hi = p;
        if (hi <= lo) hi = lo + 1;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var c = line.Open())
        using (var a = area.Open())
        {
            bool open = false; double lastX = 0;
            for (int i = 0; i < _pts.Count; i++)
            {
                double v = _pts[i];
                double x = i / (double)(_pts.Count - 1) * w;
                if (double.IsNaN(v))
                {
                    if (open) { a.LineTo(new Point(lastX, h), false, false); open = false; }
                    continue;
                }
                var p = new Point(x, h - (Math.Clamp(v, lo, hi) - lo) / (hi - lo) * (h - 1));
                if (!open) { c.BeginFigure(p, false, false); a.BeginFigure(new Point(x, h), true, true); a.LineTo(p, false, false); open = true; }
                else { c.LineTo(p, true, true); a.LineTo(p, false, false); }
                lastX = x;
            }
            if (open) a.LineTo(new Point(lastX, h), false, false);
        }
        line.Freeze(); area.Freeze();
        dc.DrawGeometry(Palette.B(Stroke, 0.14), null, area);
        dc.DrawGeometry(null, Palette.P(Stroke, 1.6), line);
    }
}

/// <summary>Horizontal bar 0..1, coloured green → amber → red as it fills (or a fixed colour).</summary>
public sealed class UsageBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(UsageBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    /// <summary>When set, always this colour instead of the traffic-light scale.</summary>
    public Color? Fixed { get; set; }

    protected override Size MeasureOverride(Size s) => new(double.IsInfinity(s.Width) ? 120 : s.Width, 8);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = Math.Min(8, ActualHeight);
        double y = (ActualHeight - h) / 2;
        dc.DrawRoundedRectangle(Palette.B(Palette.Line), null, new Rect(0, y, w, h), h / 2, h / 2);
        double v = Math.Clamp(double.IsNaN(Value) ? 0 : Value, 0, 1);
        var col = Fixed ?? (v < 0.6 ? Palette.Good : v < 0.85 ? Palette.Fair : Palette.Bad);
        if (v > 0) dc.DrawRoundedRectangle(Palette.B(col), null, new Rect(0, y, Math.Max(h, w * v), h), h / 2, h / 2);
    }
}

/// <summary>
/// "This needs administrator rights" strip with a Restart-as-admin button. Hidden automatically when already
/// elevated. Set <see cref="Section"/> so WinScope reopens on the same tool.
/// </summary>
public sealed class AdminNotice : Border
{
    private readonly TextBlock _text;
    public string Section { get; set; } = Sections.Home;

    public string Message
    {
        get => _text.Text;
        set => _text.Text = value;
    }

    public AdminNotice()
    {
        Background = Palette.B(Palette.Fair, 0.08);
        BorderBrush = Palette.B(Palette.Fair, 0.4);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 8, 12, 8);
        var dock = new DockPanel();
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Foreground = Palette.B(Palette.Text) };
        var btn = new Button { Content = "Restart as admin", Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(12, 0, 0, 0) };
        string? original = null;
        btn.Click += (_, _) =>
        {
            original ??= _text.Text;
            if (Elevation.RestartAsAdmin(Section) is string why)
                _text.Text = original + "\n" + why;
        };
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        dock.Children.Add(_text);
        Child = dock;
        if (Elevation.IsAdmin) Visibility = Visibility.Collapsed;
    }
}

/// <summary>true ↔ false, for "hit-testable while the popup is closed".</summary>
public sealed class NotBoolConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type t, object p, System.Globalization.CultureInfo c) => value is not true;
    public object ConvertBack(object value, Type t, object p, System.Globalization.CultureInfo c) => value is not true;
}
