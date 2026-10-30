using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using WinScope.Core;
using WinScope.UI;

namespace WinScope.Modules.Storage;

internal sealed class PreviewRow
{
    public required string Name { get; init; }
    public required string Folder { get; init; }
    public long Size { get; init; }
    public required string SizeText { get; init; }
    public DateTime Modified { get; init; }
    public required string ModifiedText { get; init; }
    public required string FullPath { get; init; }
}

/// <summary>One category: tick box, what it is, how much, and a preview of its largest files.</summary>
internal sealed class CategoryRow : Border
{
    private readonly CheckBox? _check;
    private readonly TextBlock _size, _count, _notes, _previewHead;
    private readonly ToggleButton? _previewBtn;
    private readonly StackPanel _preview;
    private readonly DataGrid? _grid;
    private CategoryResult? _result;
    private bool _updating;

    public CategoryDef Def { get; }
    public event Action<CategoryRow, bool>? TickChanged;

    public CategoryRow(CategoryDef def, bool ticked, Action<string>? reviewFolder)
    {
        Def = def;
        Background = Palette.B(Palette.Panel);
        BorderBrush = Palette.B(Palette.Line);
        BorderThickness = new Thickness(1);
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(12, 10, 12, 10);
        Margin = new Thickness(0, 0, 0, 8);

        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(120) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        if (!def.ReportOnly)
        {
            _check = new CheckBox { IsChecked = ticked, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 0, 0), IsEnabled = false };
            _check.Checked += (_, _) => { if (!_updating) TickChanged?.Invoke(this, true); };
            _check.Unchecked += (_, _) => { if (!_updating) TickChanged?.Invoke(this, false); };
            AutomationName(_check, def.Name);
            g.Children.Add(_check);
        }

        var info = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(Ui.T(def.Name, 13.5, semi: true));
        bool locked = (def.AdminOnly || def.PartAdmin) && !Elevation.IsAdmin;
        if (locked)
            title.Children.Add(new TextBlock
            {
                Text = Ui.LockGlyph, FontFamily = Ui.IconFont, FontSize = 12, Foreground = Palette.B(Palette.Fair),
                Margin = new Thickness(8, 2, 0, 0), VerticalAlignment = VerticalAlignment.Center,
                ToolTip = def.AdminOnly ? "Needs administrator rights" : "Part of this needs administrator rights",
            });
        info.Children.Add(title);
        info.Children.Add(Ui.T(def.Description, 12, Ui.Muted, wrap: true));
        if (def.Caution != null)
        {
            var c = Ui.T("! " + def.Caution, 12, Palette.B(Palette.Fair), wrap: true, opacity: 0.85);
            c.Margin = new Thickness(0, 2, 0, 0);
            info.Children.Add(c);
        }
        _notes = Ui.T("", 12, Palette.B(Palette.Weak), wrap: true);
        _notes.Visibility = Visibility.Collapsed;
        _notes.Margin = new Thickness(0, 2, 0, 0);
        info.Children.Add(_notes);
        Grid.SetColumn(info, 1);
        g.Children.Add(info);

        var sizes = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 12, 0) };
        _size = Ui.T("Checking…", 14, semi: true);
        _size.HorizontalAlignment = HorizontalAlignment.Right;
        _count = Ui.T("", 11.5, Ui.Muted);
        _count.HorizontalAlignment = HorizontalAlignment.Right;
        sizes.Children.Add(_size);
        sizes.Children.Add(_count);
        Grid.SetColumn(sizes, 2);
        g.Children.Add(sizes);

        FrameworkElement action;
        if (def.Kind == CatKind.RecycleBin)
        {
            var b = Ui.Btn("Open Recycle Bin", tip: "See what's in it (opens Explorer)");
            b.Click += (_, _) => Ui.OpenRecycleBin();
            action = b;
        }
        else if (def.Kind == CatKind.ReportWindowsOld)
        {
            var b = Ui.Btn("Open Storage settings", tip: "Settings › System › Storage › Temporary files");
            b.Click += (_, _) => Ui.OpenStorageSettings();
            action = b;
        }
        else if (def.Kind == CatKind.ReportDownloads)
        {
            var b = Ui.Btn("Review largest downloads", tip: "Opens the space analyser on your Downloads folder");
            b.Click += (_, _) => { if (_result?.Folder is string f) reviewFolder?.Invoke(f); };
            action = b;
        }
        else
        {
            _previewBtn = new ToggleButton { Content = "Preview", Style = Ui.Style("Chip"), ToolTip = "Show the largest files this would remove", Margin = new Thickness(0) };
            _previewBtn.Checked += (_, _) => ShowPreview(true);
            _previewBtn.Unchecked += (_, _) => ShowPreview(false);
            action = _previewBtn;
        }
        action.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(action, 3);
        g.Children.Add(action);

        _preview = new StackPanel { Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
        _previewHead = Ui.T("", 12, Ui.Muted);
        _previewHead.Margin = new Thickness(0, 0, 0, 6);
        _preview.Children.Add(_previewHead);
        if (_previewBtn != null)
        {
            _grid = new DataGrid { MaxHeight = 260, SelectionMode = DataGridSelectionMode.Single, RowStyle = Ui.RowTip("FullPath") };
            _grid.Columns.Add(Ui.Col("Name", "Name", 0));
            _grid.Columns.Add(Ui.Col("Folder", "Folder", 260));
            _grid.Columns.Add(Ui.Col("Size", "SizeText", 80, "Size", right: true));
            _grid.Columns.Add(Ui.Col("Modified", "ModifiedText", 100, "Modified"));
            _preview.Children.Add(_grid);
        }
        Grid.SetRow(_preview, 1);
        Grid.SetColumn(_preview, 1);
        Grid.SetColumnSpan(_preview, 3);
        g.Children.Add(_preview);

        Child = g;
    }

    private static void AutomationName(DependencyObject d, string name) =>
        System.Windows.Automation.AutomationProperties.SetName(d, name);

    public bool IsTickedAndCleanable => _check?.IsChecked == true && _result?.Cleanable == true;

    public void Update(CategoryResult? r, bool ticked, bool busy)
    {
        _result = r;
        _updating = true;
        try
        {
            if (r == null)
            {
                _size.Text = "Checking…";
                _count.Text = "";
                SetNotes(Array.Empty<string>());
                if (_check != null) { _check.IsEnabled = false; _check.IsChecked = ticked; }
                return;
            }
            if (Def.ReportOnly)
            {
                _size.Text = r.Present ? Format.Bytes(r.Bytes) : "Not found";
                _count.Text = r.Present ? Ui.Files(r.Files) : "";
                SetNotes(r.Notes);
                return;
            }
            _size.Text = r.Files > 0 ? Format.Bytes(r.Bytes) : "—";
            _count.Text = Def.Kind == CatKind.RecycleBin
                ? (r.Files == 0 ? "Empty" : r.Files == 1 ? "1 item" : $"{Ui.N(r.Files)} items")
                : (r.Files == 0 ? "Nothing to clean" : Ui.Files(r.Files));
            SetNotes(r.Notes);
            if (_check != null)
            {
                _check.IsEnabled = r.Cleanable && !busy;
                _check.IsChecked = ticked && r.Cleanable;
            }
            if (_preview.Visibility == Visibility.Visible) FillPreview();
        }
        finally { _updating = false; }
    }

    private void SetNotes(IReadOnlyList<string> notes)
    {
        _notes.Text = string.Join("\n", notes);
        _notes.Visibility = notes.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowPreview(bool on)
    {
        _preview.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (on) FillPreview();
        else if (_grid != null) _grid.ItemsSource = null;
    }

    private void FillPreview()
    {
        var r = _result;
        if (r == null) { _previewHead.Text = "Still checking…"; if (_grid != null) _grid.ItemsSource = null; return; }
        if (r.Files == 0) _previewHead.Text = "Nothing to clean right now.";
        else if (r.Files > r.Top.Count) _previewHead.Text = $"The {r.Top.Count} largest of {Ui.N(r.Files)} files ({Format.Bytes(r.Bytes)} in total):";
        else _previewHead.Text = $"{Ui.Files(r.Files)} ({Format.Bytes(r.Bytes)} in total):";
        if (_grid != null)
        {
            _grid.ItemsSource = r.Top.Select(f => new PreviewRow
            {
                Name = f.Name, Folder = f.Folder, Size = f.Size, SizeText = Format.Bytes(f.Size),
                Modified = f.WriteUtc, ModifiedText = Ui.Date(f.WriteUtc), FullPath = f.Path,
            }).ToList();
            _grid.Visibility = r.Top.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        }
    }
}

/// <summary>Cleanup tab: categories with preview, then "Clean selected" → confirm → background clean.</summary>
internal sealed class CleanupView : Grid
{
    private readonly StorageService _svc;
    private readonly Dictionary<string, bool> _ticked = new();
    private readonly List<CategoryRow> _rows = new();
    private readonly TextBlock _total, _sub, _progressText, _resultText;
    private readonly Button _clean, _recheck, _cancel;
    private readonly Border _progressBox, _resultBox;
    private readonly UiPost _post, _progressPost;
    private CancellationTokenSource? _cleanCts;
    private bool _cleaning, _visible, _dirty = true, _cleanAfterAnalysis;
    private static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(5);

    public bool IsCleaning => _cleaning;

    public CleanupView(StorageService svc, Action<string> reviewFolder)
    {
        _svc = svc;
        _post = new UiPost(Dispatcher);
        _progressPost = new UiPost(Dispatcher);
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // Header: total + actions
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _recheck = Ui.Btn("Check again", tip: "Measure every category again");
        _recheck.Margin = new Thickness(0, 0, 8, 0);
        _recheck.Click += (_, _) => _svc.Cleanup.Analyse();
        _clean = Ui.Btn("Clean selected…", accent: true, tip: "Shows exactly what will be deleted and asks before doing anything");
        _clean.Click += async (_, _) => await CleanClicked();
        actions.Children.Add(_recheck);
        actions.Children.Add(_clean);
        DockPanel.SetDock(actions, Dock.Right);
        head.Children.Add(actions);
        var totals = new StackPanel();
        _total = Ui.T("Checking what can be cleaned…", 18, Palette.B(Palette.Accent), semi: true);
        _sub = Ui.T("", 12, Ui.Muted);
        totals.Children.Add(_total);
        totals.Children.Add(_sub);
        head.Children.Add(totals);
        Children.Add(head);

        var notice = new AdminNotice
        {
            Section = Sections.Storage, Margin = new Thickness(0, 0, 0, 10),
            Message = "Windows temporary files, system crash dumps, system error reports and the Windows Update cache need " +
                      "administrator rights to check and clean. Everything else works as you are.",
        };
        SetRow(notice, 1);
        Children.Add(notice);

        // Progress / result strip
        var strip = new StackPanel();
        _progressText = Ui.T("", 12.5);
        _progressText.VerticalAlignment = VerticalAlignment.Center;
        _cancel = Ui.Btn("Cancel", tip: "Stop after the current file");
        _cancel.Click += (_, _) => { try { _cleanCts?.Cancel(); } catch { } _cancel.IsEnabled = false; _progressText.Text += " – stopping…"; };
        var pd = new DockPanel();
        DockPanel.SetDock(_cancel, Dock.Right);
        pd.Children.Add(_cancel);
        pd.Children.Add(_progressText);
        _progressBox = Strip(pd, Palette.Accent);
        _resultText = Ui.T("", 12.5, wrap: true);
        var close = Ui.Btn("✓ OK");
        close.Padding = new Thickness(10, 3, 10, 3);
        close.VerticalAlignment = VerticalAlignment.Top;
        close.Click += (_, _) => _resultBox!.Visibility = Visibility.Collapsed;
        var rd = new DockPanel();
        DockPanel.SetDock(close, Dock.Right);
        rd.Children.Add(close);
        rd.Children.Add(_resultText);
        _resultBox = Strip(rd, Palette.Good);
        strip.Children.Add(_progressBox);
        strip.Children.Add(_resultBox);
        SetRow(strip, 2);
        Children.Add(strip);

        // Categories
        var list = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
        foreach (var d in _svc.Cleanup.Defs.Where(d => !d.ReportOnly))
        {
            _ticked[d.Id] = d.DefaultOn;
            var row = new CategoryRow(d, d.DefaultOn, reviewFolder);
            row.TickChanged += (r, on) => { _ticked[r.Def.Id] = on; UpdateTotals(); };
            _rows.Add(row);
            list.Children.Add(row);
        }
        var info = Ui.H("For your information – WinScope doesn't delete these");
        info.Margin = new Thickness(2, 10, 0, 8);
        list.Children.Add(info);
        foreach (var d in _svc.Cleanup.Defs.Where(d => d.ReportOnly))
        {
            var row = new CategoryRow(d, false, reviewFolder);
            _rows.Add(row);
            list.Children.Add(row);
        }
        var log = Ui.T(UserFiles.WriteAllowed
                ? "Every clean is recorded in " + CleanupEngine.LogPath
                : "Running as administrator, so the cleanup log isn't saved to disk – it's shown after each clean instead.", 11.5, Ui.Muted, wrap: true);
        log.Margin = new Thickness(2, 4, 0, 4);
        list.Children.Add(log);
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        SetRow(scroll, 3);
        Children.Add(scroll);

        _svc.Cleanup.Changed += () => _post.Post(OnEngineChanged);
    }

    private static Border Strip(UIElement child, Color c) => new()
    {
        Background = Palette.B(c, 0.07), BorderBrush = Palette.B(c, 0.35), BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10),
        Child = child, Visibility = Visibility.Collapsed,
    };

    public void SetVisible(bool v)
    {
        _visible = v;
        if (v && _dirty) Refresh();
    }

    private void OnEngineChanged()
    {
        // "Clean selected" found the figures stale: once the fresh analysis lands, ask again with the new numbers.
        var snap = _svc.Cleanup.Snapshot;
        if (_cleanAfterAnalysis && !snap.Analysing && !snap.Cleaning)
        {
            _cleanAfterAnalysis = false;
            Refresh();
            _ = CleanClicked();
            return;
        }
        if (!_visible) { _dirty = true; return; }
        Refresh();
    }

    private void Refresh()
    {
        _dirty = false;
        var snap = _svc.Cleanup.Snapshot;
        bool busy = _cleaning || snap.Cleaning;
        foreach (var row in _rows)
        {
            snap.Results.TryGetValue(row.Def.Id, out var r);
            row.Update(r, _ticked.TryGetValue(row.Def.Id, out bool t) && t, busy);
        }
        _recheck.IsEnabled = !busy && !snap.Analysing;
        _recheck.Content = snap.Analysing ? "Checking…" : "Check again";
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var snap = _svc.Cleanup.Snapshot;
        if (snap.SafeBytes is long safe)
            _total.Text = safe > 0 ? $"≈{Format.Bytes(safe)} can be cleaned safely" : "Nothing much to clean right now";
        else
            _total.Text = "Checking what can be cleaned…";

        var chosen = _rows.Where(r => r.IsTickedAndCleanable).ToList();
        long bytes = chosen.Sum(r => snap.Results.TryGetValue(r.Def.Id, out var x) ? x.Bytes : 0);
        _sub.Text = chosen.Count == 0
            ? "Tick the categories you want to clean. Nothing is deleted until you confirm."
            : $"Selected: {chosen.Count} {(chosen.Count == 1 ? "category" : "categories")} · {Format.Bytes(bytes)}. Nothing is deleted until you confirm.";
        _clean.IsEnabled = chosen.Count > 0 && !_cleaning && !snap.Cleaning && !snap.Analysing;
    }

    private async Task CleanClicked()
    {
        try
        {
            var snap = _svc.Cleanup.Snapshot;
            if (_cleaning || snap.Analysing || snap.Cleaning) return;

            // Old figures? Measure again first, then confirm with fresh numbers.
            if (snap.AnalysedUtc is not DateTime at || DateTime.UtcNow - at > StaleAfter)
            {
                _cleanAfterAnalysis = true;
                _sub.Text = "The figures were more than 5 minutes old, so WinScope is checking again – you'll be asked to confirm next.";
                _svc.Cleanup.Analyse();
                return;
            }

            var chosen = _rows.Where(r => r.IsTickedAndCleanable)
                .Select(r => (def: r.Def, res: snap.Results.TryGetValue(r.Def.Id, out var x) ? x : null))
                .Where(c => c.res is { Cleanable: true })
                .Select(c => (c.def, res: c.res!)).ToList();
            if (chosen.Count == 0) return;

            var requests = chosen.Select(c => new CleanRequest(c.def, c.res.Files, c.res.Bytes, c.res.ReadyRoots.ToList())).ToList();
            long totalBytes = chosen.Sum(c => c.res.Bytes), totalFiles = chosen.Sum(c => c.res.Files);
            if (!Confirm.Show(this, "Clean up these files?", ConfirmBody(chosen, totalFiles, totalBytes), "Delete " + Format.Bytes(totalBytes), 680))
                return;

            // Recycle Bin: re-query now; if more went in since the preview, show the new figures and ask again.
            int bin = requests.FindIndex(r => r.Def.Kind == CatKind.RecycleBin);
            if (bin >= 0)
            {
                var q = await Task.Run(() => CleanupEngine.QueryRecycleBin(_svc.Tasks.Token));
                if (q is not (long qb, long qi)) { requests.RemoveAt(bin); _sub.Text = "Couldn't check the Recycle Bin again, so it will be left alone."; }
                else if (qi > requests[bin].PreviewFiles || qb > requests[bin].PreviewBytes)
                {
                    var again = Ui.T($"Since the preview, more went into the Recycle Bin: it now holds {Ui.N(qi)} items ({Format.Bytes(qb)}). " +
                                     "Emptying it removes them for good. Empty it now?", 12.5, wrap: true);
                    if (Confirm.Show(this, "The Recycle Bin has changed", again, "Empty " + Format.Bytes(qb), 520))
                        requests[bin] = requests[bin] with { PreviewFiles = qi, PreviewBytes = qb };
                    else requests.RemoveAt(bin);
                }
                if (requests.Count == 0) return;
            }

            _cleaning = true;
            _cleanCts?.Dispose();
            _cleanCts = CancellationTokenSource.CreateLinkedTokenSource(_svc.Tasks.Token);
            var token = _cleanCts.Token;
            _resultBox.Visibility = Visibility.Collapsed;
            _progressBox.Visibility = Visibility.Visible;
            _cancel.IsEnabled = true;
            _progressText.Text = "Starting…";
            Refresh();

            var hwnd = Ui.OwnerHandle(this);
            int logStart = _svc.Cleanup.SessionLog.Count;
            List<CleanOutcome>? outcomes;
            try
            {
                // Tracked by the TaskBag, so closing or restarting cancels it and waits for the current file.
                outcomes = await _svc.Tasks.ComputeAsync(_ => _svc.Cleanup.CleanAsync(requests, hwnd, p =>
                {
                    var (cat, f, b, sk) = (p.Category, p.Files, p.Bytes, p.Skipped);
                    _progressPost.Post(() =>
                        _progressText.Text = $"Cleaning {cat}… {Ui.Files(f)} · {Format.Bytes(b)} freed" + (sk > 0 ? $" · {Ui.N(sk)} skipped" : ""));
                }, token));
            }
            finally
            {
                _cleaning = false;
                _progressBox.Visibility = Visibility.Collapsed;
            }
            ShowResult(outcomes ?? new List<CleanOutcome>(), token.IsCancellationRequested, logStart);
            _svc.RefreshDrivesSoon();
            _svc.Cleanup.Analyse();
            Refresh();
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            _cleaning = false;
            _progressBox.Visibility = Visibility.Collapsed;
            _resultText.Text = "Cleaning stopped because of an error: " + ex.Message;
            _resultBox.Visibility = Visibility.Visible;
            Refresh();
        }
    }

    private static UIElement ConfirmBody(List<(CategoryDef def, CategoryResult res)> chosen, long files, long bytes)
    {
        var sp = new StackPanel();
        sp.Children.Add(Ui.T("WinScope will delete the following. Permanently deleted files can't be restored.", 12.5, wrap: true));
        var g = new Grid { Margin = new Thickness(0, 12, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(170) });
        int row = 0;
        void Add(string a, string b, string c, string d, bool head = false, bool bold = false, Brush? dColour = null)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            string[] v = { a, b, c, d };
            for (int i = 0; i < 4; i++)
            {
                var t = Ui.T(v[i], head ? 11.5 : 12.5, head ? Ui.Muted : (i == 3 ? dColour ?? Ui.Text : Ui.Text), semi: head || bold);
                t.Margin = new Thickness(0, 3, 8, 0);
                if (i is 1 or 2) t.TextAlignment = TextAlignment.Right;
                SetRow(t, row); SetColumn(t, i);
                g.Children.Add(t);
            }
            row++;
        }
        void Folders(IEnumerable<string> roots)
        {
            var list = roots.ToList();
            if (list.Count == 0) return;
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var t = Ui.T(string.Join("\n", list.Select(r => "   " + r)), 11, Ui.Muted, wrap: true);
            t.Margin = new Thickness(0, 0, 0, 4);
            t.ToolTip = "Only files inside these folders are touched";
            SetRow(t, row); SetColumnSpan(t, 4);
            g.Children.Add(t);
            row++;
        }
        Add("Category", "Files", "Size", "What happens", head: true);
        foreach (var (def, res) in chosen)
        {
            string count = def.Kind == CatKind.RecycleBin ? $"{Ui.N(res.Files)} items" : Ui.N(res.Files);
            string what = def.Kind == CatKind.RecycleBin ? "Emptied – can't be restored" : "Permanently deleted";
            Add(def.Name, count, Format.Bytes(res.Bytes), what, dColour: Palette.B(Palette.Weak));
            Folders(def.Kind == CatKind.RecycleBin ? new[] { "The Recycle Bin on every drive" } : res.ReadyRoots);
        }
        Add("Total", "≈" + Ui.N(files), "≈" + Format.Bytes(bytes), "", bold: true);
        var scroller = new ScrollViewer { Content = g, MaxHeight = 380, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        sp.Children.Add(scroller);
        var foot = Ui.T("Every file is checked again just before it's deleted. Files that are in use, changed in the last day " +
                        "(where that applies), or not really inside these folders are skipped, and a category stops if it turns " +
                        "out much bigger than shown here. Your own documents are never touched here – they only ever go to the " +
                        "Recycle Bin, one at a time, from the space analyser.", 11.5, Ui.Muted, wrap: true);
        foot.Margin = new Thickness(0, 12, 0, 0);
        sp.Children.Add(foot);
        return sp;
    }

    private void ShowResult(List<CleanOutcome> outcomes, bool cancelled, int logStart)
    {
        long bytes = outcomes.Sum(o => o.Bytes), files = outcomes.Sum(o => o.Files);
        long pending = outcomes.Sum(o => o.PendingFiles), pendingBytes = outcomes.Sum(o => o.PendingBytes);
        var skipped = new SkipCounts();
        foreach (var o in outcomes) skipped.AddAll(o.Skipped);
        var lines = new List<string>
        {
            (cancelled ? "Stopped. " : "Done. ") + $"Freed {Format.Bytes(bytes)} ({Ui.Files(files)} removed).",
        };
        if (pending > 0)
            lines.Add($"{Ui.Files(pending)} ({Format.Bytes(pendingBytes)}) will be freed when the app using them closes.");
        if (skipped.Total > 0) lines.Add($"Skipped {Ui.Files(skipped.Total)}: {skipped.Describe()}.");
        foreach (var o in outcomes)
            foreach (var n in o.Notes.Distinct()) lines.Add($"{o.Def.Name}: {n}");
        if (UserFiles.WriteAllowed) lines.Add("Details were added to " + CleanupEngine.LogPath);
        else
        {
            lines.Add("Log for this clean (not saved to disk while running as administrator):");
            lines.AddRange(_svc.Cleanup.SessionLog.Skip(logStart).Take(20).Select(l => "   " + l.Replace('\t', ' ')));
        }
        _resultText.Text = string.Join("\n", lines);
        _resultBox.Visibility = Visibility.Visible;
    }

    public void Stop()
    {
        try { _cleanCts?.Cancel(); } catch { }
    }
}
