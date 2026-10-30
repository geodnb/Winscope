using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using WinScope.Core;
using WinScope.UI;

namespace WinScope;

/// <summary>
/// Shell: a Home dashboard with one card per tool (each tool's compact summary), and a full view per tool.
/// Tools run their background collection all the time; the shell only tells them what's visible.
/// </summary>
public partial class MainWindow : Window
{
    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<IToolModule> _modules = new();
    private readonly Dictionary<string, RadioButton> _nav = new();
    private readonly string? _startSection;
    private Grid _home = null!;
    private string _current = Sections.Home;
    private bool _closed;
    private WindowState _lastShownState = WindowState.Normal;

    public MainWindow(string? startSection)
    {
        InitializeComponent();
        _startSection = Sections.IsValid(startSection) ? startSection : null;
        // Restore the size, but never bigger than this screen's work area (a size saved on a larger monitor
        // could otherwise push the title bar off screen).
        var wa = SystemParameters.WorkArea;
        // On small or high-scaling screens (e.g. 1366×768, or 1080p at 150%) the usable height is under 760:
        // let the window shrink to fit rather than hang off the screen; the dashboard summaries have compact modes.
        MinHeight = Math.Min(MinHeight, wa.Height);
        MinWidth = Math.Min(MinWidth, wa.Width);
        Width = Math.Min(_settings.WindowWidth ?? Width, wa.Width);
        Height = Math.Min(_settings.WindowHeight ?? Height, wa.Height);
        if (_settings.Maximised) WindowState = WindowState.Maximized;
        Elevation.BusyCheck = () => _modules.Select(SafeBusy).FirstOrDefault(r => r != null);

        foreach (var m in CreateModules()) _modules.Add(m);
        BuildNav();
        BuildHome();
        ShowAdminState();
        InitReportSettings();

        SourceInitialized += (_, _) => DarkTitleBar(this);
        Loaded += (_, _) => OnLoaded();
        Closing += OnClosing;
        StateChanged += (_, _) =>
        {
            if (WindowState != WindowState.Minimized) _lastShownState = WindowState;
            UpdateVisibility();
        };
        PreviewKeyDown += (_, e) =>
        {
            // Alt+Home (or Alt+Left) returns to the dashboard, like a browser.
            if (Keyboard.Modifiers == ModifierKeys.Alt && (e.SystemKey is Key.Home or Key.Left)) { Navigate(Sections.Home); e.Handled = true; }
        };
    }

    /// <summary>The tools, in dashboard order (top-left, top-right, bottom-left, bottom-right).</summary>
    private static IEnumerable<IToolModule> CreateModules()
    {
        yield return Create(() => new Modules.Performance.PerformanceModule(), Sections.Performance, "Performance");
        yield return Create(() => new Modules.Network.NetworkModule(), Sections.Network, "Network");
        yield return Create(() => new Modules.Events.EventsModule(), Sections.Events, "Events");
        yield return Create(() => new Modules.Storage.StorageModule(), Sections.Storage, "Storage");
    }

    /// <summary>A tool that fails to construct shows its error in its card instead of taking the app down.</summary>
    private static IToolModule Create(Func<IToolModule> make, string id, string title)
    {
        try { return make(); }
        catch (Exception ex) { App.LogError(ex); return new FailedModule(id, title, ex); }
    }

    // ================================================================== navigation

    private void BuildNav()
    {
        AddNav(Sections.Home, "Home");
        foreach (var m in _modules) AddNav(m.Id, m.Title);

        void AddNav(string id, string title)
        {
            var rb = new RadioButton { Content = title, GroupName = "nav", Style = (Style)FindResource("ChipRadio"), Margin = new Thickness(0, 0, 6, 0) };
            rb.Click += (_, _) => Navigate(id);
            _nav[id] = rb;
            NavBar.Children.Add(rb);
        }
    }

    private void Navigate(string id)
    {
        if (!Sections.IsValid(id)) id = Sections.Home;
        var module = _modules.FirstOrDefault(m => m.Id == id);
        if (id != Sections.Home && module == null) id = Sections.Home;
        _current = id;
        _settings.LastSection = id;
        Host.Content = id == Sections.Home ? _home : module!.FullView;
        if (_nav.TryGetValue(id, out var rb)) rb.IsChecked = true;
        UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        bool shown = WindowState != WindowState.Minimized && !_closed;
        foreach (var m in _modules)
        {
            try { m.SetVisibility(shown && _current == Sections.Home, shown && _current == m.Id); }
            catch (Exception ex) { App.LogError(ex); }
        }
    }

    // ================================================================== dashboard

    private void BuildHome()
    {
        _home = new Grid();
        _home.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _home.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        _home.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _home.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _home.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        _home.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        for (int i = 0; i < _modules.Count && i < 4; i++)
        {
            var card = BuildCard(_modules[i]);
            Grid.SetRow(card, (i / 2) * 2);
            Grid.SetColumn(card, (i % 2) * 2);
            _home.Children.Add(card);
        }
    }

    private Border BuildCard(IToolModule m)
    {
        var accent = Palette.B(Palette.Accent);
        var card = new Border { Style = (Style)FindResource("Card"), Cursor = Cursors.Hand, Padding = new Thickness(16, 14, 16, 14) };
        var dock = new DockPanel();

        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        DockPanel.SetDock(head, Dock.Top);
        var open = new TextBlock { Text = "Open  ›", Foreground = Palette.B(Palette.Muted), FontSize = 12, VerticalAlignment = VerticalAlignment.Top };
        DockPanel.SetDock(open, Dock.Right);
        head.Children.Add(open);
        var icon = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), Background = (Brush)FindResource("AccentSoft"),
            Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock { Text = m.Icon, Foreground = accent, FontSize = 15, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        DockPanel.SetDock(icon, Dock.Left);
        head.Children.Add(icon);
        var titles = new StackPanel();
        titles.Children.Add(new TextBlock { Text = m.Title.ToUpperInvariant(), FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Palette.B(Palette.Text) });
        titles.Children.Add(new TextBlock { Text = m.Tagline, FontSize = 11.5, Foreground = Palette.B(Palette.Muted), TextTrimming = TextTrimming.CharacterEllipsis });
        head.Children.Add(titles);
        dock.Children.Add(head);

        FrameworkElement body;
        try { body = m.Summary; }
        catch (Exception ex) { App.LogError(ex); body = new TextBlock { Text = "Couldn't load: " + ex.Message, TextWrapping = TextWrapping.Wrap, Foreground = Palette.B(Palette.Bad) }; }
        dock.Children.Add(body);
        card.Child = dock;

        card.MouseEnter += (_, _) => { card.BorderBrush = Palette.B(Palette.Accent, 0.55); open.Foreground = accent; };
        card.MouseLeave += (_, _) => { card.BorderBrush = (Brush)FindResource("LineBrush"); open.Foreground = Palette.B(Palette.Muted); };
        card.MouseLeftButtonUp += (_, e) =>
        {
            if (IsInsideInteractive(e.OriginalSource as DependencyObject, card)) return;   // let buttons/lists inside work
            Navigate(m.Id);
        };
        m.OpenRequested += () => Navigate(m.Id);
        return card;
    }

    /// <summary>True if the click landed on a button, checkbox, list item, text box or scrollbar inside the card.</summary>
    private static bool IsInsideInteractive(DependencyObject? d, DependencyObject stop)
    {
        while (d != null && d != stop)
        {
            if (d is ButtonBase or TextBoxBase or ScrollBar or ListBoxItem or ComboBox or Thumb) return true;
            d = d is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return false;
    }

    // ================================================================== reports

    private void InitReportSettings()
    {
        ReportOutputBox.ItemsSource = new[] { "Copy it to the clipboard", "Save it as a text file", "Both" };
        ReportOutputBox.SelectedIndex = (int)_settings.ReportOutput;
        RedactCheck.IsChecked = _settings.RedactReports;
        SettingsNote.Text = (UserFiles.WriteAllowed
            ? "Reports are saved to Documents\\WinScope Reports. "
            : "While running as administrator, reports are only copied to the clipboard and settings changes aren't saved. ")
            + $"WinScope {Version}.";
    }

    private void ReportOutput_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ReportOutputBox.SelectedIndex >= 0) _settings.ReportOutput = (ReportOutput)ReportOutputBox.SelectedIndex;
    }

    private void Redact_Click(object sender, RoutedEventArgs e) => _settings.RedactReports = RedactCheck.IsChecked == true;

    private void SettingsPopup_Closed(object? sender, EventArgs e)
    {
        SettingsBtn.IsChecked = false;
        _settings.Save();
    }

    private void OpenReports_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!UserFiles.WriteAllowed) { SettingsNote.Text = "Not available while running as administrator."; return; }
            Directory.CreateDirectory(ReportService.ReportsFolder);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe")
                { ArgumentList = { ReportService.ReportsFolder }, UseShellExecute = false });
        }
        catch (Exception ex) { SettingsNote.Text = "Couldn't open the folder: " + ex.Message; }
    }

    /// <summary>Report menu: full report first, then the current tool (if one is open), then every tool.</summary>
    private void Report_Click(object sender, RoutedEventArgs e)
    {
        ReportMenu.Items.Clear();
        var current = _modules.FirstOrDefault(m => m.Id == _current);
        AddItem("Full system report", "Everything: this PC, key findings and all four tools", null);
        if (current != null) AddItem($"{current.Title} report", $"Just the {current.Title} tool", current);
        ReportMenu.Items.Add(new Separator { Margin = new Thickness(4), Background = (Brush)FindResource("LineBrush") });
        foreach (var m in _modules.Where(m => m != current)) AddItem($"{m.Title} report", null, m);
        ReportMenu.PlacementTarget = ReportBtn;
        ReportMenu.IsOpen = true;

        void AddItem(string header, string? tip, IToolModule? only)
        {
            var mi = new MenuItem { Header = header, ToolTip = tip };
            mi.Click += (_, _) => MakeReport(only);
            ReportMenu.Items.Add(mi);
        }
    }

    private bool _reporting;

    private async void MakeReport(IToolModule? only)
    {
        if (_reporting) return;
        var o = new ReportOptions { Redact = _settings.RedactReports, NowUtc = DateTime.UtcNow };
        var tools = (only != null ? new[] { only } : _modules.ToArray())
            .Select(m => (m.Title, (Func<ReportOptions, ReportSection?>)(opt => m.BuildReport(opt))));
        string text;
        try { text = ReportService.Compose(tools, o, only?.Title); }
        catch (Exception ex) { App.LogError(ex); StatusLeft.Text = "Couldn't make the report: " + ex.Message; return; }
        _reporting = true;
        ReportBtn.IsEnabled = false;
        try
        {
            var (msg, _) = await ReportService.DeliverAsync(text, _settings.ReportOutput, only?.Title ?? "system");
            if (_closed) return;
            // The warning goes first: the status line is trimmed with "…" when it's long.
            StatusLeft.Text = (o.Redact ? "" : "Personal details are INCLUDED – turn on \"Hide personal details\" in ⚙ before sharing. ") + msg;
            StatusLeft.ToolTip = StatusLeft.Text;
        }
        catch (Exception ex) { App.LogError(ex); if (!_closed) StatusLeft.Text = "Couldn't deliver the report: " + ex.Message; }
        finally { _reporting = false; if (!_closed) ReportBtn.IsEnabled = true; }
    }

    // ================================================================== admin

    private void ShowAdminState()
    {
        AdminBadge.Visibility = Elevation.IsAdmin ? Visibility.Visible : Visibility.Collapsed;   // header space is tight
        if (Elevation.IsAdmin)
        {
            AdminBadge.Background = Palette.B(Palette.Fair, 0.1);
            AdminBadge.BorderBrush = Palette.B(Palette.Fair, 0.5);
            AdminBadgeText.Text = Elevation.IsDesktopUser ? "Administrator" : "Administrator · other account";
            AdminBadgeText.Foreground = Palette.B(Palette.Fair);
            AdminBadge.ToolTip = Elevation.IsDesktopUser
                ? "Running with administrator rights. Settings changes aren't saved in this mode."
                : $"Running as {Elevation.AccountName}, not the account signed in to Windows – so cleanup of your own files is turned off.";
            ElevateBtn.Visibility = Visibility.Collapsed;
            StatusRight.Text = $"WinScope {Version} · running as administrator";
        }
        else
        {
            AdminBadge.Background = Brushes.Transparent;
            AdminBadge.BorderBrush = (Brush)FindResource("LineBrush");
            AdminBadgeText.Text = "Not elevated";
            AdminBadgeText.Foreground = Palette.B(Palette.Muted);
            AdminBadge.ToolTip = "WinScope is running without administrator rights. Everything works except per-app network speeds and system cache cleanup.";
            StatusRight.Text = $"WinScope {Version} · not elevated";
        }
    }

    private static string Version => typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";

    private void Elevate_Click(object sender, RoutedEventArgs e)
    {
        if (Elevation.RestartAsAdmin(_current) is string why)
            StatusLeft.Text = why;
    }

    // ================================================================== lifecycle

    private void OnLoaded()
    {
        Redactor.Warm();   // look up names and folders to hide in reports now, off the UI thread
        foreach (var m in _modules)
        {
            try { m.Start(); }
            catch (Exception ex) { App.LogError(ex); StatusLeft.Text = $"{m.Title} couldn't start: {ex.Message}"; }
        }
        Navigate(_startSection ?? _settings.LastSection);
    }

    private static string? SafeBusy(IToolModule m)
    {
        try { return m.BusyReason; } catch { return null; }
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        _closed = true;
        App.Closing = true;
        // Remember maximised even when closed from the taskbar while minimised.
        _settings.Maximised = (WindowState == WindowState.Minimized ? _lastShownState : WindowState) == WindowState.Maximized;
        var rb = RestoreBounds;
        if (WindowState == WindowState.Normal) { _settings.WindowWidth = Width; _settings.WindowHeight = Height; }
        else if (!rb.IsEmpty) { _settings.WindowWidth = rb.Width; _settings.WindowHeight = rb.Height; }
        _settings.Save();
        // Disappear straight away, tell every tool to stop at once (so they wind down in parallel), then dispose.
        Hide();
        foreach (var m in _modules)
        {
            try { m.BeginStop(); }
            catch (Exception ex) { App.LogError(ex); }
        }
        foreach (var m in _modules)
        {
            try { m.Dispose(); }
            catch (Exception ex) { App.LogError(ex); }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    internal static void DarkTitleBar(Window w)
    {
        try
        {
            var h = new WindowInteropHelper(w).Handle;
            int on = 1;
            if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)   // DWMWA_USE_IMMERSIVE_DARK_MODE
                DwmSetWindowAttribute(h, 19, ref on, sizeof(int));        // pre-20H1 value
        }
        catch { }
    }

    /// <summary>Stand-in for a tool whose constructor threw.</summary>
    private sealed class FailedModule : IToolModule
    {
        private readonly string _why;

        public FailedModule(string id, string title, Exception ex)
        {
            Id = id; Title = title; _why = ex.GetBaseException().Message;
            var t = new TextBlock { Text = $"{title} couldn't start:\n{ex.Message}", TextWrapping = TextWrapping.Wrap, Foreground = Palette.B(Palette.Bad) };
            Summary = t;
            FullView = new Border { Style = (Style)Application.Current.FindResource("Card"), Child = new TextBlock { Text = t.Text, TextWrapping = TextWrapping.Wrap, Foreground = Palette.B(Palette.Bad) } };
        }
        public string Id { get; }
        public string Title { get; }
        public string Tagline => "Unavailable";
        public string Icon => "!";
        public FrameworkElement Summary { get; }
        public FrameworkElement FullView { get; }
        public void Start() { }
        public void SetVisibility(bool s, bool f) { }
        public event Action? OpenRequested { add { } remove { } }
        public void Dispose() { }

        public ReportSection? BuildReport(ReportOptions options) => new()
        {
            Title = Title,
            Findings = new[] { $"The {Title} tool couldn't start, so this report has no {Title} data." },
            Body = $"The {Title} tool couldn't start: {(options.Redact ? Redactor.Apply(_why) : _why)}",
        };
    }
}
