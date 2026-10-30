using System.Windows;
using System.Windows.Controls;
using WinScope.UI;

namespace WinScope.Modules.Storage;

/// <summary>Full view: drive tiles on top, then "Cleanup" and "Space analyser" tabs.</summary>
internal sealed class StorageView : Grid
{
    private readonly StorageService _svc;
    private readonly StackPanel _drives;
    private readonly RadioButton _tabCleanup, _tabAnalyser;
    private readonly ContentControl _host;
    private readonly CleanupView _cleanup;
    private readonly AnalyserView _analyser;
    private readonly UiPost _post;
    private bool _visible, _drivesDirty = true, _analyserTab;

    public StorageView(StorageService svc)
    {
        _svc = svc;
        _post = new UiPost(Dispatcher);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        _drives = new StackPanel { Orientation = Orientation.Horizontal };
        var scroll = new ScrollViewer
        {
            Content = _drives, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Margin = new Thickness(0, 0, 0, 10),
        };
        Children.Add(scroll);

        var tabs = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        _tabCleanup = new RadioButton { Content = "Cleanup", GroupName = "storageTabs", Style = Ui.Style("ChipRadio"), IsChecked = true, Margin = new Thickness(0, 0, 6, 0) };
        _tabAnalyser = new RadioButton { Content = "Space analyser", GroupName = "storageTabs", Style = Ui.Style("ChipRadio") };
        _tabCleanup.Checked += (_, _) => Switch(false);
        _tabAnalyser.Checked += (_, _) => Switch(true);
        var note = Ui.T("Nothing is deleted until you confirm", 11.5, Ui.Muted);
        note.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(note, Dock.Right);
        tabs.Children.Add(note);
        var tabStack = new StackPanel { Orientation = Orientation.Horizontal };
        tabStack.Children.Add(_tabCleanup);
        tabStack.Children.Add(_tabAnalyser);
        tabs.Children.Add(tabStack);
        SetRow(tabs, 1);
        Children.Add(tabs);

        _analyser = new AnalyserView(svc);
        _cleanup = new CleanupView(svc, folder => ShowAnalyser(folder, scan: true));
        _host = new ContentControl { Content = _cleanup, Focusable = false };
        SetRow(_host, 2);
        Children.Add(_host);

        _svc.DrivesChanged += () => _post.Post(OnDrivesChanged);
    }

    private void Switch(bool analyser)
    {
        _analyserTab = analyser;
        _host.Content = analyser ? _analyser : _cleanup;
        ApplyVisibility();
    }

    public void ShowCleanup() => _tabCleanup.IsChecked = true;

    public void ShowAnalyser(string? path, bool scan)
    {
        _tabAnalyser.IsChecked = true;
        if (path != null) _analyser.SetPath(path, scan);
    }

    public void SetVisible(bool v)
    {
        _visible = v;
        if (v && _drivesDirty) BuildDrives();
        ApplyVisibility();
        // Coming back after a while: measure again so the numbers are fresh.
        var snap = _svc.Cleanup.Snapshot;
        if (v && !snap.Analysing && !snap.Cleaning && snap.AnalysedUtc is DateTime at && DateTime.UtcNow - at > TimeSpan.FromMinutes(5))
            _svc.Cleanup.Analyse();
    }

    private void ApplyVisibility()
    {
        _cleanup.SetVisible(_visible && !_analyserTab);
        _analyser.SetVisible(_visible && _analyserTab);
    }

    private void OnDrivesChanged()
    {
        if (!_visible) { _drivesDirty = true; return; }
        BuildDrives();
    }

    private void BuildDrives()
    {
        _drivesDirty = false;
        _drives.Children.Clear();
        var list = _svc.Drives;
        if (list.Count == 0)
        {
            _drives.Children.Add(Ui.T(_svc.DrivesLoaded ? "No drives found." : "Reading drives…", 12, Ui.Muted));
            return;
        }
        foreach (var d in list) _drives.Children.Add(Tile(d));
    }

    private Button Tile(DriveSnap d)
    {
        var g = new Grid { Width = 214 };
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var top = new DockPanel();
        var fs = Ui.T(d.FileSystem, 11, Ui.Muted);
        DockPanel.SetDock(fs, Dock.Right);
        top.Children.Add(fs);
        top.Children.Add(Ui.T($"{d.Letter}  {d.Label}", 13, semi: true));
        g.Children.Add(top);
        var bar = new UsageBar { Value = d.UsedFraction, Margin = new Thickness(0, 6, 0, 5) };
        SetRow(bar, 1);
        g.Children.Add(bar);
        var free = Ui.T($"{Format.Bytes(d.Free)} free of {Format.Bytes(d.Total)}", 11.5, Ui.Muted);
        SetRow(free, 2);
        g.Children.Add(free);
        var b = new Button
        {
            Content = g, Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 8, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ToolTip = $"{d.Letter} {d.Label} – {Format.Pct(d.UsedFraction * 100)} used. Click to analyse this drive.",
        };
        string root = d.Root;
        b.Click += (_, _) => ShowAnalyser(root, scan: false);
        return b;
    }

    public void Stop()
    {
        _cleanup.Stop();
        _analyser.Stop();
    }

    /// <summary>For the shell's restart guard.</summary>
    public string? BusyReason =>
        _cleanup.IsCleaning || _svc.Cleanup.Snapshot.Cleaning ? "a cleanup is running"
        : _analyser.IsScanning ? "a scan is running"
        : _analyser.IsRecycling ? "a file is being moved to the Recycle Bin"
        : null;
}

/// <summary>
/// Dashboard card body: drives (C: first) and how much can be cleaned. Never deletes anything. Shows only as many
/// drive rows as fit (measured on resize); the cleanup panel with its button is always visible.
/// </summary>
internal sealed class StorageSummary : Grid
{
    private const double RowHeight = 40, MoreHeight = 18;
    private readonly StorageService _svc;
    private readonly StackPanel _drives;
    private readonly Border _drivesHost;
    private readonly TextBlock _safe, _safeSub;
    private readonly UiPost _post;
    private bool _visible, _dirty = true;
    private int _capacity = 3;

    public event Action? ReviewRequested;

    public StorageSummary(StorageService svc)
    {
        _svc = svc;
        _post = new UiPost(Dispatcher);
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star), MinHeight = 0 });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        _drives = new StackPanel { VerticalAlignment = VerticalAlignment.Top };
        _drivesHost = new Border { Child = _drives, ClipToBounds = true };
        _drivesHost.SizeChanged += (_, e) =>
        {
            int cap = Math.Max(0, (int)((e.NewSize.Height + 0.5) / RowHeight));
            if (cap != _capacity) { _capacity = cap; Refresh(); }
        };
        Children.Add(_drivesHost);

        var panel = new Border
        {
            Background = Palette.B(Palette.Panel2), CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 4, 0, 0),
        };
        var dock = new DockPanel();
        var review = Ui.Btn("Review cleanup", tip: "Open the cleanup list – nothing is deleted from here");
        review.VerticalAlignment = VerticalAlignment.Center;
        review.Margin = new Thickness(10, 0, 0, 0);
        review.Click += (_, _) => ReviewRequested?.Invoke();
        DockPanel.SetDock(review, Dock.Right);
        dock.Children.Add(review);
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        _safe = Ui.T("Checking…", 15, Palette.B(Palette.Accent), semi: true);
        _safeSub = Ui.T("Temp files, caches and old reports · nothing is deleted until you confirm", 11.5, Ui.Muted);
        texts.Children.Add(_safe);
        texts.Children.Add(_safeSub);
        dock.Children.Add(texts);
        panel.Child = dock;
        SetRow(panel, 1);
        Children.Add(panel);

        _svc.DrivesChanged += () => _post.Post(OnChanged);
        _svc.Cleanup.Changed += () => _post.Post(OnChanged);
        Refresh();
    }

    public void SetVisible(bool v)
    {
        _visible = v;
        if (v && _dirty) Refresh();
    }

    private void OnChanged()
    {
        if (!_visible) { _dirty = true; return; }
        Refresh();
    }

    private void Refresh()
    {
        _dirty = false;
        _drives.Children.Clear();
        var list = _svc.Drives;
        double h = _drivesHost.ActualHeight > 0 ? _drivesHost.ActualHeight : RowHeight * _capacity;
        if (list.Count == 0)
            _drives.Children.Add(Ui.T(_svc.DrivesLoaded ? "No drives found." : "Reading drives…", 12.5, Ui.Muted));
        else
        {
            int fit = Math.Max(0, (int)(h / RowHeight));
            int show = list.Count <= fit ? list.Count : Math.Max(0, (int)((h - MoreHeight) / RowHeight));
            show = Math.Min(show, list.Count);
            foreach (var d in list.Take(show)) _drives.Children.Add(DriveRow(d));
            if (list.Count > show && h >= MoreHeight)
            {
                int more = list.Count - show;
                var t = Ui.T(show == 0 ? $"{list.Count} drives – open Storage to see them"
                                       : $"+ {more} more {(more == 1 ? "drive" : "drives")}", 11.5, Ui.Muted);
                t.Height = MoreHeight;
                _drives.Children.Add(t);
            }
        }

        var snap = _svc.Cleanup.Snapshot;
        if (snap.SafeBytes is long b)
            _safe.Text = b > 0 ? $"≈{Format.Bytes(b)} can be cleaned safely" : "Nothing much to clean right now";
        else
            _safe.Text = "Checking…";
    }

    private static FrameworkElement DriveRow(DriveSnap d)
    {
        var sp = new StackPanel { Height = RowHeight };
        var top = new DockPanel();
        var right = Ui.T($"{Format.Bytes(d.Used)} used of {Format.Bytes(d.Total)}", 12, Ui.Muted);
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(Ui.T($"{d.Letter}  {d.Label}", 12.5, semi: true));
        sp.Children.Add(top);
        sp.Children.Add(new UsageBar { Value = d.UsedFraction, Margin = new Thickness(0, 5, 0, 0) });
        return sp;
    }
}
