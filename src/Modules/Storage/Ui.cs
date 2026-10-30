using System.Diagnostics;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinScope.UI;

namespace WinScope.Modules.Storage;

/// <summary>Small factories so the code-built views stay readable. Styles come from Themes/Dark.xaml.</summary>
internal static class Ui
{
    public static readonly CultureInfo C = CultureInfo.InvariantCulture;
    public static readonly FontFamily IconFont = new("Segoe Fluent Icons, Segoe MDL2 Assets");
    public const string LockGlyph = "";      // Segoe MDL2/Fluent "Lock"

    public static Style Style(string key) => (Style)Application.Current.FindResource(key);
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Brush Text => Palette.B(Palette.Text);
    public static Brush Muted => Palette.B(Palette.Muted);

    public static TextBlock T(string text, double size = 13, Brush? fg = null, bool semi = false, bool wrap = false, double opacity = 1) => new()
    {
        Text = text, FontSize = size, Foreground = fg ?? Text, Opacity = opacity,
        FontWeight = semi ? FontWeights.SemiBold : FontWeights.Normal,
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
    };

    public static TextBlock H(string text) => new() { Text = text.ToUpperInvariant(), Style = Style("H") };

    public static Button Btn(string text, bool accent = false, string? tip = null)
    {
        var b = new Button { Content = text, ToolTip = tip };
        if (accent) b.Style = Style("AccentButton");
        return b;
    }

    public static Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Style = Style("Card"), Child = child, Padding = padding ?? new Thickness(14),
    };

    public static string N(long v) => v.ToString("N0", C);

    public static string Files(long n) => n == 1 ? "1 file" : $"{N(n)} files";

    public static string Date(DateTime utc) =>
        utc == DateTime.MinValue ? "—" : utc.ToLocalTime().ToString("d MMM yyyy", C);

    public static IntPtr OwnerHandle(DependencyObject from)
    {
        try
        {
            var w = Window.GetWindow(from) ?? Application.Current?.MainWindow;
            return w == null ? IntPtr.Zero : new WindowInteropHelper(w).Handle;
        }
        catch { return IntPtr.Zero; }
    }

    public static DataGridTextColumn Col(string header, string path, double width, string? sortPath = null, bool right = false)
    {
        var col = new DataGridTextColumn
        {
            Header = header, Binding = new System.Windows.Data.Binding(path),
            Width = width > 0 ? new DataGridLength(width) : new DataGridLength(1, DataGridLengthUnitType.Star),
            SortMemberPath = sortPath ?? path,
        };
        var es = new Style(typeof(TextBlock));
        es.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (right) es.Setters.Add(new Setter(TextBlock.TextAlignmentProperty, TextAlignment.Right));
        col.ElementStyle = es;
        return col;
    }

    /// <summary>Row style with a tooltip bound to <paramref name="path"/>, based on the theme's DataGridRow style.</summary>
    public static Style RowTip(string path)
    {
        var s = new Style(typeof(DataGridRow), (Style)Application.Current.FindResource(typeof(DataGridRow)));
        s.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new System.Windows.Data.Binding(path)));
        return s;
    }

    // ------------------------------------------------------------------ shell launches (read-only actions)

    private static string Explorer => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>
    /// Open a folder in Explorer – only after checking it really is a folder, and always as an argument to
    /// explorer.exe ("/e,"), never by shell-executing the path (which would run a file swapped in its place).
    /// </summary>
    public static bool OpenFolder(string path)
    {
        if (!SafeArg(path)) return false;
        try
        {
            var a = File.GetAttributes(path);
            if ((a & FileAttributes.Directory) == 0) return false;
        }
        catch { return false; }
        return Launch(Explorer, "/e,\"" + path + "\"");
    }

    /// <summary>Open the containing folder with the item selected (Explorer never runs it).</summary>
    public static bool SelectInExplorer(string path) => SafeArg(path) && Launch(Explorer, "/select,\"" + path + "\"");

    public static void OpenRecycleBin() => Launch(Explorer, "shell:RecycleBinFolder");

    public static void OpenStorageSettings()
    {
        try { Process.Start(new ProcessStartInfo("ms-settings:storagesense") { UseShellExecute = true })?.Dispose(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>A fully-qualified path with nothing that could break out of the quoted argument.</summary>
    private static bool SafeArg(string path) =>
        !string.IsNullOrEmpty(path) && path.IndexOfAny(new[] { '"', '\0', '\r', '\n' }) < 0 && Path.IsPathFullyQualified(path);

    private static bool Launch(string exe, string args)
    {
        try { Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false })?.Dispose(); return true; }
        catch (Exception ex) { App.LogError(ex); return false; }
    }
}

/// <summary>Thin "share of parent" bar for list rows.</summary>
internal sealed class ShareBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(ShareBar), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    private static readonly Brush Track = Palette.B(Palette.Line);
    private static readonly Brush Fill = Palette.B(Palette.Accent2);

    protected override Size MeasureOverride(Size s) => new(double.IsInfinity(s.Width) ? 100 : s.Width, 8);

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = Math.Min(7, ActualHeight), y = (ActualHeight - h) / 2;
        if (w <= 0) return;
        dc.DrawRoundedRectangle(Track, null, new Rect(0, y, w, h), h / 2, h / 2);
        double v = Math.Clamp(double.IsNaN(Value) ? 0 : Value, 0, 1);
        if (v > 0) dc.DrawRoundedRectangle(Fill, null, new Rect(0, y, Math.Max(h, w * v), h), h / 2, h / 2);
    }
}

/// <summary>Dark modal confirmation. Cancel is the default; the action button must be clicked deliberately.</summary>
internal static class Confirm
{
    public static bool Show(DependencyObject owner, string title, UIElement body, string okText, double width = 560)
    {
        var w = new Window
        {
            Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Style = Ui.Style("AppWindow"),
        };
        try { w.Owner = Window.GetWindow(owner) ?? Application.Current.MainWindow; } catch { }
        w.SourceInitialized += (_, _) => MainWindow.DarkTitleBar(w);

        bool result = false;
        var ok = Ui.Btn(okText, accent: true);
        var cancel = Ui.Btn("Cancel");
        cancel.IsCancel = true;
        cancel.Margin = new Thickness(0, 0, 8, 0);
        ok.Click += (_, _) => { result = true; w.Close(); };
        cancel.Click += (_, _) => w.Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(20, 16, 20, 18) };
        root.Children.Add(Ui.T(title, 16, semi: true));
        var bodyHost = new ContentControl { Content = body, Margin = new Thickness(0, 10, 0, 0), Focusable = false };
        root.Children.Add(bodyHost);
        root.Children.Add(buttons);
        w.Content = root;
        w.Loaded += (_, _) => cancel.Focus();
        w.PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { w.Close(); e.Handled = true; } };
        w.ShowDialog();
        return result;
    }
}
