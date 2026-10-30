using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using WinScope.UI;

namespace WinScope.Modules.Storage;

internal sealed class ChildRow
{
    public required string Display { get; init; }
    public long Size { get; init; }
    public required string SizeText { get; init; }
    public double Share { get; init; }
    public required string ShareText { get; init; }
    public long Files { get; init; }
    public required string FilesText { get; init; }
    public DirNode? Node { get; init; }
    public bool IsLink { get; init; }
    public required string Tip { get; init; }
}

internal sealed class FileRow
{
    public required BigFile File { get; init; }
    public required string Name { get; init; }
    public long Size { get; init; }
    public required string SizeText { get; init; }
    public DateTime Modified { get; init; }
    public required string ModifiedText { get; init; }
    public required string FullPath { get; init; }
}

/// <summary>Space analyser: scan a drive or folder, drill into folders by size, review the largest files.</summary>
internal sealed class AnalyserView : Grid
{
    private readonly StorageService _svc;
    private readonly TextBox _path;
    private readonly Button _browse, _scan, _cancel, _back, _up, _openHere, _show, _recycle;
    private readonly TextBlock _status, _hint, _fileMsg, _largestSub;
    private readonly WrapPanel _crumbs;
    private readonly DataGrid _children, _largest;
    private readonly DispatcherTimer _timer;
    private readonly Stack<DirNode> _history = new();
    private ScanResult? _result;
    private DirNode? _current;
    private ScanProgress? _progress;
    private CancellationTokenSource? _scanCts;
    private bool _visible, _scanning, _recycling;

    public bool IsScanning => _scanning;
    public bool IsRecycling => _recycling;

    public AnalyserView(StorageService svc)
    {
        _svc = svc;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // ---- toolbar
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _browse = Ui.Btn("Browse…", tip: "Pick a folder to scan");
        _scan = Ui.Btn("Scan", accent: true, tip: "Measure every folder below this one (read-only)");
        _cancel = Ui.Btn("Cancel");
        _cancel.IsEnabled = false;
        foreach (var b in new[] { _browse, _scan, _cancel }) b.Margin = new Thickness(8, 0, 0, 0);
        var right = new StackPanel { Orientation = Orientation.Horizontal };
        right.Children.Add(_browse); right.Children.Add(_scan); right.Children.Add(_cancel);
        DockPanel.SetDock(right, Dock.Right);
        bar.Children.Add(right);
        _path = new TextBox { Text = DefaultPath(), VerticalContentAlignment = VerticalAlignment.Center, ToolTip = "Drive or folder to scan" };
        _path.KeyDown += (_, e) => { if (e.Key == Key.Enter && !_scanning) { e.Handled = true; _ = StartScan(); } };
        bar.Children.Add(_path);
        Children.Add(bar);
        _browse.Click += (_, _) => Browse();
        _scan.Click += async (_, _) => await StartScan();
        _cancel.Click += (_, _) => { try { _scanCts?.Cancel(); } catch { } };

        _status = Ui.T("Pick a drive or folder and press Scan. Scanning only reads – nothing is changed.", 12, Ui.Muted);
        _status.Margin = new Thickness(2, 0, 0, 8);
        SetRow(_status, 1);
        Children.Add(_status);

        // ---- body: folders (left) and largest files (right)
        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star), MinWidth = 420 });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0.62, GridUnitType.Star), MinWidth = 330, MaxWidth = 560 });
        SetRow(body, 2);
        Children.Add(body);

        // left card
        var left = new DockPanel();
        var nav = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
        _back = Ui.Btn("‹ Back", tip: "Previous folder (Backspace)");
        _up = Ui.Btn("↑ Up", tip: "Parent folder");
        _openHere = Ui.Btn("Open in Explorer", tip: "Open this folder in File Explorer");
        foreach (var b in new[] { _back, _up }) { b.Padding = new Thickness(10, 4, 10, 4); b.Margin = new Thickness(0, 0, 6, 0); }
        _openHere.Padding = new Thickness(10, 4, 10, 4);
        _back.Click += (_, _) => GoBack();
        _up.Click += (_, _) => GoUp();
        _openHere.Click += (_, _) => { if (_current != null) Ui.OpenFolder(_current.FullPath); };
        DockPanel.SetDock(_back, Dock.Left); DockPanel.SetDock(_up, Dock.Left); DockPanel.SetDock(_openHere, Dock.Right);
        nav.Children.Add(_back); nav.Children.Add(_up); nav.Children.Add(_openHere);
        _crumbs = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(4, 0, 8, 0) };
        nav.Children.Add(_crumbs);
        DockPanel.SetDock(nav, Dock.Top);
        left.Children.Add(nav);

        _children = new DataGrid { SelectionMode = DataGridSelectionMode.Single, RowStyle = Ui.RowTip("Tip") };
        _children.Columns.Add(Ui.Col("Name", "Display", 0));
        var barCol = new DataGridTemplateColumn { Header = "Share", Width = new DataGridLength(110), SortMemberPath = "Size", CanUserSort = true };
        var f = new FrameworkElementFactory(typeof(ShareBar));
        f.SetBinding(ShareBar.ValueProperty, new Binding("Share"));
        f.SetValue(FrameworkElement.HeightProperty, 8.0);
        barCol.CellTemplate = new DataTemplate { VisualTree = f };
        _children.Columns.Add(barCol);
        _children.Columns.Add(Ui.Col("%", "ShareText", 52, "Size", right: true));
        _children.Columns.Add(Ui.Col("Size", "SizeText", 84, "Size", right: true));
        _children.Columns.Add(Ui.Col("Files", "FilesText", 84, "Files", right: true));
        _children.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) != null) DrillSelected();
        };
        _children.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) { DrillSelected(); e.Handled = true; }
            else if (e.Key == Key.Back) { GoBack(); e.Handled = true; }
        };
        var gridHost = new Grid();
        gridHost.Children.Add(_children);
        _hint = Ui.T("Your folders will appear here, biggest first. Double-click a folder (or press Enter) to look inside it.", 12.5, Ui.Muted, wrap: true);
        _hint.HorizontalAlignment = HorizontalAlignment.Center;
        _hint.VerticalAlignment = VerticalAlignment.Center;
        _hint.TextAlignment = TextAlignment.Center;
        _hint.MaxWidth = 380;
        gridHost.Children.Add(_hint);
        left.Children.Add(gridHost);
        var leftCard = Ui.Card(left, new Thickness(12));
        body.Children.Add(leftCard);

        // right card
        var rightDock = new DockPanel();
        var head = new StackPanel();
        var h = Ui.H("Largest files");
        h.Margin = new Thickness(0, 0, 0, 2);
        head.Children.Add(h);
        _largestSub = Ui.T("From the last scan", 11.5, Ui.Muted);
        _largestSub.Margin = new Thickness(0, 0, 0, 8);
        head.Children.Add(_largestSub);
        DockPanel.SetDock(head, Dock.Top);
        rightDock.Children.Add(head);

        var foot = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        var btns = new StackPanel { Orientation = Orientation.Horizontal };
        _show = Ui.Btn("Show in Explorer");
        _recycle = Ui.Btn("Move to Recycle Bin…", tip: "Asks first. You can restore it from the Recycle Bin later.");
        _show.Margin = new Thickness(0, 0, 8, 0);
        _show.IsEnabled = _recycle.IsEnabled = false;
        if (Recycler.WhyUnavailable is string unavailable) _recycle.ToolTip = unavailable;
        btns.Children.Add(_show); btns.Children.Add(_recycle);
        foot.Children.Add(btns);
        _fileMsg = Ui.T("", 12, Ui.Muted, wrap: true);
        _fileMsg.Margin = new Thickness(0, 6, 0, 0);
        foot.Children.Add(_fileMsg);
        if (Recycler.WhyUnavailable is string why) _fileMsg.Text = why;
        DockPanel.SetDock(foot, Dock.Bottom);
        rightDock.Children.Add(foot);

        _largest = new DataGrid { SelectionMode = DataGridSelectionMode.Single, RowStyle = Ui.RowTip("FullPath") };
        _largest.Columns.Add(Ui.Col("Name", "Name", 0));
        _largest.Columns.Add(Ui.Col("Size", "SizeText", 76, "Size", right: true));
        _largest.Columns.Add(Ui.Col("Modified", "ModifiedText", 92, "Modified"));
        _largest.SelectionChanged += (_, _) => UpdateFileButtons();
        _largest.MouseDoubleClick += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject d && FindParent<DataGridRow>(d) != null && _largest.SelectedItem is FileRow r)
                Ui.SelectInExplorer(r.FullPath);
        };
        rightDock.Children.Add(_largest);
        _show.Click += (_, _) => { if (_largest.SelectedItem is FileRow r) Ui.SelectInExplorer(r.FullPath); };
        _recycle.Click += async (_, _) => await RecycleSelected();
        var rightCard = Ui.Card(rightDock, new Thickness(12));
        Grid.SetColumn(rightCard, 2);
        body.Children.Add(rightCard);

        _timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => { if (_visible) ShowProgress(); };
        UpdateNav();
    }

    private static string DefaultPath()
    {
        try { return Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? @"C:\"; }
        catch { return @"C:\"; }
    }

    private static T? FindParent<T>(DependencyObject d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }

    public void SetVisible(bool v)
    {
        _visible = v;
        if (v && _scanning) ShowProgress();
    }

    /// <summary>Set the folder to scan (from a drive tile or "Review largest downloads"), optionally scanning straight away.</summary>
    public void SetPath(string path, bool scan)
    {
        if (_scanning)
        {
            _status.Text = "A scan is already running – cancel it first to scan " + path + ".";
            return;
        }
        _path.Text = path;
        if (scan) _ = StartScan();
    }

    private void Browse()
    {
        try
        {
            var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Pick a drive or folder to scan" };
            string cur = _path.Text.Trim();
            if (Path.IsPathFullyQualified(cur) && Directory.Exists(cur)) dlg.InitialDirectory = cur;
            if (dlg.ShowDialog(Window.GetWindow(this)) == true && !string.IsNullOrEmpty(dlg.FolderName))
                _path.Text = dlg.FolderName;
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    // ================================================================== scan

    private async Task StartScan()
    {
        if (_scanning) return;
        try
        {
            string input = _path.Text.Trim().Trim('"');
            if (input.Length == 2 && input[1] == ':') input += "\\";
            if (!Path.IsPathFullyQualified(input)) { _status.Text = "Type a full path, like C:\\ or C:\\Users."; return; }
            string full;
            try { full = Path.GetFullPath(input); } catch { _status.Text = "That path isn't valid."; return; }
            if (full.Length > 3) full = full.TrimEnd('\\');
            var check = await Task.Run(() => CheckStart(full));
            if (check != null) { _status.Text = check; return; }

            long? driveUsed = null;
            if (WinPath.Normalize(full) is string n && WinPath.IsRoot(n))
            {
                var d = _svc.Drives.FirstOrDefault(x => string.Equals(x.Root, full, StringComparison.OrdinalIgnoreCase));
                if (d != null) driveUsed = d.Used;
            }

            _scanning = true;
            _scanCts?.Dispose();
            _scanCts = CancellationTokenSource.CreateLinkedTokenSource(_svc.Tasks.Token);
            var ct = _scanCts.Token;
            _progress = new ScanProgress();
            var progress = _progress;
            UpdateNav();
            ShowProgress();
            _timer.Start();
            ScanResult? res = null;
            try
            {
                res = await _svc.Tasks.Compute(_ => SpaceScanner.Scan(full, progress, ct, StorageNative.IsUnreadable));
            }
            finally
            {
                _timer.Stop();
                _scanning = false;
            }
            if (res == null) { _status.Text = "The scan didn't finish."; UpdateNav(); return; }
            res.DriveUsed = res.Cancelled ? null : driveUsed;
            _result = res;
            _history.Clear();
            Navigate(res.Root, push: false);
            FillLargest();
            _status.Text = Summary(res);
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _scanning = false;
            _timer.Stop();
            _status.Text = "The scan stopped because of an error: " + ex.Message;
            UpdateNav();
        }
    }

    /// <summary>The start folder must exist and not itself be a link (we never follow links).</summary>
    private static string? CheckStart(string full)
    {
        try
        {
            if (!Directory.Exists(full)) return "That folder doesn't exist.";
            var a = File.GetAttributes(full);
            if (FileRules.IsLink(a) && !(WinPath.Normalize(full) is string n && WinPath.IsRoot(n)))
                return "That folder is a link to somewhere else – pick the real folder instead.";
            return null;
        }
        catch (UnauthorizedAccessException) { return "Windows won't let WinScope read that folder."; }
        catch (Exception ex) { return "Can't open that folder: " + ex.Message; }
    }

    private void ShowProgress()
    {
        var p = _progress;
        if (p == null || !_scanning) return;
        long dirs = Interlocked.Read(ref p.Dirs), files = Interlocked.Read(ref p.Files), bytes = Interlocked.Read(ref p.Bytes);
        _status.Text = $"Scanning… {Ui.N(dirs)} folders · {Ui.N(files)} files · {Format.Bytes(bytes)} · {p.Current}";
    }

    private static string Summary(ScanResult r)
    {
        var s = (r.Cancelled ? "Stopped early – these results are incomplete. " : "Scanned ") +
                $"{r.RootPath} in {Format.Span(r.Took)}: {Format.Bytes(r.Bytes)} in {Ui.Files(r.Files)} and {Ui.N(r.Dirs)} folders.";
        if (r.Unreadable > 0)
            s += $" {Ui.N(r.Unreadable)} folders couldn't be read" + (WinScope.Core.Elevation.IsAdmin ? "." : " (need administrator).");
        if (r.Links > 0) s += $" {Ui.N(r.Links)} links not followed.";
        if (r.CloudFiles > 0) s += $" {Ui.N(r.CloudFiles)} online-only files ({Format.Bytes(r.CloudBytes)}) not counted – they aren't on this disk.";
        return s;
    }

    // ================================================================== folder list

    private void Navigate(DirNode n, bool push)
    {
        if (push && _current != null && _current != n) _history.Push(_current);
        _current = n;
        var rows = new List<ChildRow>();
        long gap = 0;
        if (n.Parent == null && _result?.DriveUsed is long used && used > n.Size) gap = used - n.Size;
        double total = Math.Max(1, n.Size + gap);

        if (n.Children != null)
            foreach (var c in n.Children.OrderByDescending(c => c.Size))
            {
                rows.Add(new ChildRow
                {
                    Display = c.IsLink ? c.Name + "   (link – not followed)" : c.Name,
                    Size = c.Size, SizeText = c.IsLink ? "—" : Format.Bytes(c.Size),
                    Share = c.Size / total, ShareText = c.IsLink ? "" : Pct(c.Size / total),
                    Files = c.Files, FilesText = c.IsLink ? "" : Ui.N(c.Files),
                    Node = c, IsLink = c.IsLink,
                    Tip = c.IsLink ? "A link (junction or symbolic link) to another place. WinScope doesn't follow links, so its contents are counted where they really live."
                                   : "Double-click to look inside",
                });
            }
        if (n.OwnFiles > 0)
            rows.Add(new ChildRow
            {
                Display = "(files directly in this folder)", Size = n.OwnSize, SizeText = Format.Bytes(n.OwnSize),
                Share = n.OwnSize / total, ShareText = Pct(n.OwnSize / total), Files = n.OwnFiles, FilesText = Ui.N(n.OwnFiles),
                Tip = "Files sitting in this folder itself, not in a sub-folder",
            });
        if (gap > 0)
            rows.Add(new ChildRow
            {
                Display = "(system files and folders WinScope couldn't read)", Size = gap, SizeText = Format.Bytes(gap),
                Share = gap / total, ShareText = Pct(gap / total), FilesText = "",
                Tip = "Used space the scan couldn't see: protected Windows folders, the page file, hibernation file, " +
                      "restore points and folders that need administrator rights. Hard-linked files can also make sizes differ.",
            });
        rows.Sort((a, b) => b.Size.CompareTo(a.Size));
        _children.ItemsSource = rows;
        _children.Items.SortDescriptions.Clear();
        foreach (var col in _children.Columns) col.SortDirection = null;
        if (rows.Count > 0) _children.SelectedIndex = 0;
        _hint.Visibility = Visibility.Collapsed;
        BuildCrumbs();
        UpdateNav();
    }

    private static string Pct(double v) => (v * 100).ToString(v < 0.1 ? "0.0" : "0", Ui.C) + "%";

    private void DrillSelected()
    {
        if (_children.SelectedItem is not ChildRow r) return;
        if (r.IsLink) { _status.Text = $"“{r.Node?.Name}” is a link to another place – WinScope doesn't follow links."; return; }
        if (r.Node != null) Navigate(r.Node, push: true);
    }

    private void GoBack()
    {
        if (_history.Count > 0) Navigate(_history.Pop(), push: false);
    }

    private void GoUp()
    {
        if (_current?.Parent is DirNode p) Navigate(p, push: true);
    }

    private void BuildCrumbs()
    {
        _crumbs.Children.Clear();
        if (_current == null) return;
        var chain = new List<DirNode>();
        for (var n = _current; n != null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        var shown = chain.Count > 6 ? chain.Take(1).Concat(new DirNode?[] { null }).Concat(chain.Skip(chain.Count - 4)).ToList()
                                    : chain.Cast<DirNode?>().ToList();
        for (int i = 0; i < shown.Count; i++)
        {
            if (i > 0) _crumbs.Children.Add(new TextBlock { Text = "›", Foreground = Ui.Muted, Margin = new Thickness(5, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center });
            var node = shown[i];
            if (node == null) { _crumbs.Children.Add(new TextBlock { Text = "…", Foreground = Ui.Muted, VerticalAlignment = VerticalAlignment.Center }); continue; }
            string label = node.Parent == null ? node.Name : node.Name;
            if (node == _current)
            {
                var t = Ui.T(label, 13, semi: true);
                t.VerticalAlignment = VerticalAlignment.Center;
                t.MaxWidth = 260;
                _crumbs.Children.Add(t);
            }
            else
            {
                var b = new Button { Content = label.Length > 40 ? label[..37] + "…" : label, Padding = new Thickness(6, 2, 6, 2), ToolTip = node.FullPath };
                var target = node;
                b.Click += (_, _) => Navigate(target, push: true);
                _crumbs.Children.Add(b);
            }
        }
    }

    private void UpdateNav()
    {
        _scan.IsEnabled = !_scanning;
        _browse.IsEnabled = !_scanning;
        _path.IsEnabled = !_scanning;
        _cancel.IsEnabled = _scanning;
        _back.IsEnabled = !_scanning && _history.Count > 0;
        _up.IsEnabled = !_scanning && _current?.Parent != null;
        _openHere.IsEnabled = _current != null;
        UpdateFileButtons();
    }

    // ================================================================== largest files

    private void FillLargest()
    {
        var r = _result;
        if (r == null) { _largest.ItemsSource = null; return; }
        _largest.ItemsSource = r.Largest.Take(50).Select(f => new FileRow
        {
            File = f, Name = f.Name, Size = f.Size, SizeText = Format.Bytes(f.Size), Modified = f.WriteUtc,
            ModifiedText = Ui.Date(f.WriteUtc), FullPath = f.Path,
        }).ToList();
        _largestSub.Text = $"Top {Math.Min(50, r.Largest.Count)} in {r.RootPath} · hover for the full path";
        UpdateFileButtons();
    }

    private void UpdateFileButtons()
    {
        bool one = _largest.SelectedItem is FileRow;
        _show.IsEnabled = one;
        _recycle.IsEnabled = one && !_recycling && Recycler.WhyUnavailable == null;
    }

    private async Task RecycleSelected()
    {
        if (_recycling || _largest.SelectedItem is not FileRow row) return;
        if (Recycler.WhyUnavailable is string unavailable) { _fileMsg.Text = unavailable; return; }
        try
        {
            _recycling = true;
            UpdateFileButtons();
            var f = row.File;
            _fileMsg.Foreground = Ui.Muted;
            _fileMsg.Text = "Checking…";
            var (plan, why) = await Recycler.PrepareAsync(f);
            if (plan == null)
            {
                _fileMsg.Foreground = Palette.B(Palette.Weak);
                _fileMsg.Text = why ?? "It couldn't be checked.";
                return;
            }
            _fileMsg.Text = "";

            var body = new StackPanel();
            body.Children.Add(Ui.T($"Move “{f.Name}” ({Format.Bytes(f.Size)}) to the Recycle Bin?", 13, wrap: true));
            var where = Ui.T(Path.GetDirectoryName(plan.FinalRaw) ?? f.Folder, 12, Ui.Muted, wrap: true);
            where.Margin = new Thickness(0, 4, 0, 0);
            body.Children.Add(where);
            if (plan.OneDrive)
            {
                var od = Ui.T("⚠ This file is in your OneDrive. Deleting it here also deletes it from OneDrive and your other devices " +
                              "(OneDrive keeps its own recycle bin for a while).", 12, Palette.B(Palette.Fair), wrap: true);
                od.Margin = new Thickness(0, 10, 0, 0);
                body.Children.Add(od);
            }
            var note = Ui.T("You can restore it from the Recycle Bin until the bin is emptied. If Windows would have to delete it " +
                            "for good instead, WinScope stops and nothing is removed.", 11.5, Ui.Muted, wrap: true);
            note.Margin = new Thickness(0, 10, 0, 0);
            body.Children.Add(note);
            if (!Confirm.Show(this, "Move to Recycle Bin?", body, "Move to Recycle Bin", 520)) return;

            _fileMsg.Text = "Moving…";
            string? err = await Recycler.MoveAsync(f, plan, Ui.OwnerHandle(this));
            if (err == null)
            {
                if (_result != null) SpaceScanner.Forget(_result, f);
                FillLargest();
                if (_current != null) Navigate(_current, push: false);
                if (_result != null) _status.Text = Summary(_result);
                _fileMsg.Foreground = Palette.B(Palette.Good);
                _fileMsg.Text = $"✓ Moved “{f.Name}” to the Recycle Bin.";
                _svc.RefreshDrivesSoon();
                _svc.Cleanup.Analyse();
            }
            else
            {
                _fileMsg.Foreground = Palette.B(Palette.Weak);
                _fileMsg.Text = err;
            }
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _fileMsg.Text = "Something went wrong: " + ex.Message;
        }
        finally
        {
            _recycling = false;
            UpdateFileButtons();
        }
    }

    public void Stop()
    {
        _timer.Stop();
        try { _scanCts?.Cancel(); } catch { }
    }
}
