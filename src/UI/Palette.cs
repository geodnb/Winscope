using System.Windows;
using System.Windows.Media;

namespace WinScope.UI;

/// <summary>Colours shared by XAML and the hand-drawn graphs.</summary>
public static class Palette
{
    public static readonly Color Bg = C("#0E1116");
    public static readonly Color Panel = C("#161A21");
    public static readonly Color Panel2 = C("#1C212A");
    public static readonly Color Line = C("#2A313C");
    public static readonly Color Grid = C("#20262F");
    public static readonly Color Text = C("#E6EAF0");
    public static readonly Color Muted = C("#8A94A3");
    public static readonly Color Accent = C("#3DD6C6");
    public static readonly Color Accent2 = C("#7AA2FF");
    public static readonly Color Rx = C("#C792EA");
    public static readonly Color Good = C("#4ADE80");
    public static readonly Color Fair = C("#FACC15");
    public static readonly Color Weak = C("#FB923C");
    public static readonly Color Bad = C("#F87171");

    private static readonly Color[] Series =
    {
        C("#3DD6C6"), C("#7AA2FF"), C("#F59E0B"), C("#F472B6"), C("#A3E635"), C("#C084FC"),
        C("#38BDF8"), C("#FB7185"), C("#FACC15"), C("#34D399"), C("#E879F9"), C("#FDBA74"),
        C("#60A5FA"), C("#BEF264"), C("#F9A8D4"), C("#5EEAD4"),
    };

    public static Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    public static SolidColorBrush B(Color c, double alpha = 1)
    {
        var b = new SolidColorBrush(Color.FromArgb((byte)(alpha * 255), c.R, c.G, c.B));
        b.Freeze();
        return b;
    }

    public static Pen P(Color c, double thickness, double alpha = 1, DashStyle? dash = null)
    {
        var p = new Pen(B(c, alpha), thickness) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (dash != null) p.DashStyle = dash;
        p.Freeze();
        return p;
    }

    /// <summary>Stable colour per SSID (FNV-1a hash), so a network keeps its colour across views and bands.</summary>
    public static Color ForName(string name)
    {
        uint h = 2166136261;
        foreach (char ch in name) { h ^= ch; h *= 16777619; }
        return Series[h % (uint)Series.Length];
    }

    public static Color ForIndex(int i) => Series[((i % Series.Length) + Series.Length) % Series.Length];

    public static Color ForRssi(double rssi) => rssi switch
    {
        >= -55 => Good,
        >= -65 => C("#A3E635"),
        >= -72 => Fair,
        >= -80 => Weak,
        _ => Bad
    };

    private static readonly Typeface Ui = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface UiSemi = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static FormattedText Text_(string s, double size, Color c, double dpi, bool semibold = false, double alpha = 1) =>
        new(s, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            semibold ? UiSemi : Ui, size, B(c, alpha), dpi);
}
