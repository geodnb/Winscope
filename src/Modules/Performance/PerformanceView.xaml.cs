using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using WinScope.UI;

namespace WinScope.Modules.Performance;

/// <summary>
/// Full view: time window chips + Live, four stacked graphs, a "moment" panel (now, or a pinned second),
/// the "Right now" app table and the spike log. All refreshes come from the module at 1 Hz while visible.
/// </summary>
public partial class PerformanceView : UserControl
{
    private readonly Sampler _sampler;
    private readonly PerfGraphs _graphs;
    private readonly ObservableCollection<AppRow> _rows = new();
    private readonly Dictionary<string, AppRow> _rowByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AppRow> _gone = new();
    private readonly ObservableCollection<SpikeRow> _spikeRows = new();
    private readonly Dictionary<int, SpikeRow> _spikeById = new();
    private readonly ListCollectionView _rowView;
    private SpikeInfo[] _spikes = Array.Empty<SpikeInfo>();
    private DateTime? _pinned;
    private SpikeInfo? _pinnedSpike;
    private DateTime _shownMoment;
    private bool _vramShown;
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    internal PerformanceView(Sampler sampler)
    {
        _sampler = sampler;
        InitializeComponent();

        _graphs = new PerfGraphs { History = sampler.History, WindowSeconds = 300 };
        _graphs.MomentClicked += t => Pin(t, null, center: false);
        _graphs.ViewScrolled += _ => UpdateLiveUi();
        GraphHost.Child = _graphs;

        Win5.Checked += (_, _) => SetWindow(5 * 60);
        Win15.Checked += (_, _) => SetWindow(15 * 60);
        Win60.Checked += (_, _) => SetWindow(60 * 60);
        Win120.Checked += (_, _) => SetWindow(120 * 60);
        LiveChip.Click += (_, _) => GoLive();
        ClearPin.Click += (_, _) => GoLive();

        AppsGrid.ItemsSource = _rows;
        _rowView = (ListCollectionView)CollectionViewSource.GetDefaultView(_rows);
        _rowView.IsLiveSorting = true;
        foreach (var p in new[] { nameof(AppRow.Cpu), nameof(AppRow.Gpu), nameof(AppRow.Ram), nameof(AppRow.Io), nameof(AppRow.VRam) })
            _rowView.LiveSortingProperties.Add(p);
        AppsGrid.Sorting += OnSorting;
        SortBy(ColCpu, ListSortDirection.Descending);
        ColVRam.Visibility = Visibility.Collapsed;

        SpikeList.ItemsSource = _spikeRows;
        SpikeList.PreviewMouseLeftButtonUp += OnSpikeClick;
        UpdateLiveUi();
    }

    /// <summary>The module forwards the shell's visibility hint (drives the graph's redraw timer).</summary>
    internal void SetActive(bool active) => _graphs.Active = active;

    // ================================================================== refresh (1 Hz while visible)

    internal void Refresh(LiveData? d, SpikeInfo[]? spikes)
    {
        if (spikes != null)
        {
            _spikes = spikes;
            _graphs.Spikes = spikes;
            UpdateSpikes(spikes);
        }
        if (d == null) { _graphs.DataChanged(); return; }
        _graphs.Gpu = d.Gpu;
        _graphs.DiskAvailable = d.DiskAvailable;
        _graphs.Supported = d.Supported;
        _graphs.DataChanged();
        UpdateApps(d);
        if (_pinned == null) ShowMoment(_sampler.History.GetMoment(d.Utc, TimeSpan.FromSeconds(1)), pinned: false);
        else if (_shownMoment == default) ShowMoment(_sampler.History.GetMoment(_pinned.Value, TimeSpan.FromSeconds(1.5)), pinned: true);
        else MomentSub.Text = PinnedSub(_pinned.Value);
        UpdateStatus(d);
    }

    private void UpdateStatus(LiveData d)
    {
        var oldest = _sampler.History.OldestUtc;
        string rec = oldest is DateTime o
            ? $"Recording since {PerfClock.ToLocal(o).ToString("HH:mm", Inv)} ({Format.Span(PerfClock.Now - o)}, keeps the last 2 h)"
            : "Starting…";
        string self = double.IsNaN(d.SelfCpu) ? "" : $" · WinScope itself: {AppRow.Pct(d.SelfCpu)} CPU";
        string gpu = d.Gpu == GpuStatus.Available && d.GpuEvery > 1 ? $" · GPU read every {d.GpuEvery} s" : "";
        StatusText.Text = rec + self + gpu;
        StatusText.ToolTip = "WinScope samples once a second in the background, even while this page is closed, so the history is ready when you need it.";
    }

    // ================================================================== right now table

    private void UpdateApps(LiveData d)
    {
        _seen.Clear();
        foreach (var a in d.Apps)
        {
            if (!_seen.Add(a.Name)) continue;
            if (!_rowByName.TryGetValue(a.Name, out var row))
            {
                row = new AppRow(a.Name);
                row.Update(a);
                _rowByName[a.Name] = row;
                _rows.Add(row);
            }
            else row.Update(a);
        }
        _gone.Clear();
        foreach (var r in _rows) if (!_seen.Contains(r.Name)) _gone.Add(r);
        foreach (var r in _gone) { _rows.Remove(r); _rowByName.Remove(r.Name); }

        if (d.VRamAvailable && !_vramShown) { _vramShown = true; ColVRam.Visibility = Visibility.Visible; }
        AppsInfo.Text = d.Supported ? $"{d.Apps.Length} apps · {d.ProcessCount} processes · {d.Cpus} logical CPUs"
            : "Per-app details aren't available on this build (needs the 64-bit WinScope)";
    }

    private void OnSorting(object? sender, DataGridSortingEventArgs e)
    {
        e.Handled = true;
        var col = e.Column;
        bool isName = col.SortMemberPath == nameof(AppRow.Name);
        // Numbers sort biggest-first on the first click; names A–Z.
        var dir = col.SortDirection == null
            ? (isName ? ListSortDirection.Ascending : ListSortDirection.Descending)
            : col.SortDirection == ListSortDirection.Ascending ? ListSortDirection.Descending : ListSortDirection.Ascending;
        SortBy(col, dir);
    }

    private void SortBy(DataGridColumn col, ListSortDirection dir)
    {
        foreach (var c in AppsGrid.Columns) if (c != col) c.SortDirection = null;
        col.SortDirection = dir;
        using (_rowView.DeferRefresh())
        {
            _rowView.SortDescriptions.Clear();
            _rowView.SortDescriptions.Add(new SortDescription(col.SortMemberPath, dir));
            if (col.SortMemberPath != nameof(AppRow.Name))
                _rowView.SortDescriptions.Add(new SortDescription(nameof(AppRow.Name), ListSortDirection.Ascending));
        }
    }

    // ================================================================== spikes list

    private void UpdateSpikes(SpikeInfo[] spikes)
    {
        var ids = new HashSet<int>();
        foreach (var s in spikes) ids.Add(s.Id);
        for (int i = _spikeRows.Count - 1; i >= 0; i--)
            if (!ids.Contains(_spikeRows[i].Id)) { _spikeById.Remove(_spikeRows[i].Id); _spikeRows.RemoveAt(i); }
        // snapshot is newest first; new spikes are always newer than the ones we have
        for (int i = spikes.Length - 1; i >= 0; i--)
        {
            var s = spikes[i];
            if (_spikeById.TryGetValue(s.Id, out var row)) row.Update(s);
            else
            {
                row = new SpikeRow(s);
                _spikeById[s.Id] = row;
                _spikeRows.Insert(0, row);
            }
        }
        bool any = _spikeRows.Count > 0;
        SpikeList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        SpikesEmpty.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        SpikeCount.Text = any ? (_spikeRows.Count == 1 ? "1 recorded" : $"{_spikeRows.Count} recorded") : "";
    }

    private void OnSpikeClick(object sender, MouseButtonEventArgs e)
    {
        if (ItemsControl.ContainerFromElement(SpikeList, e.OriginalSource as DependencyObject) is not ListBoxItem item) return;
        if (item.DataContext is not SpikeRow row) return;
        Pin(row.Info.PeakUtc, row.Info, center: true);
    }

    // ================================================================== pin / live / window

    private void SetWindow(double seconds)
    {
        if (_graphs == null) return;   // Checked fires during InitializeComponent
        _graphs.WindowSeconds = seconds;
        if (_graphs.ViewEndUtc != null && _pinned is DateTime p) _graphs.ViewEndUtc = CenteredEnd(p);
        UpdateLiveUi();
    }

    private DateTime? CenteredEnd(DateTime t)
    {
        var end = t.AddSeconds(_graphs.WindowSeconds / 2);
        var now = PerfClock.Now;
        return end >= now ? now : end;
    }

    private void Pin(DateTime utc, SpikeInfo? spike, bool center)
    {
        _pinned = utc;
        _pinnedSpike = spike;
        _graphs.PinnedUtc = utc;
        var end = _graphs.ViewEndUtc ?? PerfClock.Now;
        bool visible = utc >= end.AddSeconds(-_graphs.WindowSeconds) && utc <= end;
        // Freeze the view so the pinned line doesn't scroll away; re-centre when it's off screen.
        if (center || !visible) _graphs.ViewEndUtc = CenteredEnd(utc);
        else if (_graphs.ViewEndUtc == null) _graphs.ViewEndUtc = PerfClock.Now;
        _shownMoment = default;
        ShowMoment(_sampler.History.GetMoment(utc, TimeSpan.FromSeconds(1.5)), pinned: true);
        UpdateLiveUi();
    }

    private void GoLive()
    {
        _pinned = null;
        _pinnedSpike = null;
        _graphs.PinnedUtc = null;
        _graphs.ViewEndUtc = null;
        _shownMoment = default;
        UpdateLiveUi();
        if (_sampler.History.NewestUtc is DateTime n) ShowMoment(_sampler.History.GetMoment(n, TimeSpan.FromSeconds(1)), pinned: false);
    }

    private void UpdateLiveUi()
    {
        bool live = _graphs.ViewEndUtc == null;
        LiveChip.IsChecked = live;
        LiveText.Text = live ? "Live" : "Back to live";
        LiveDot.Fill = live ? (Brush)FindResource("GoodBrush") : (Brush)FindResource("Muted");
        ClearPin.Visibility = _pinned != null ? Visibility.Visible : Visibility.Collapsed;
    }

    // ================================================================== moment panel

    private static string PinnedSub(DateTime utc) => $"{Format.Ago(PerfClock.ToLocal(utc))} · pinned until you clear it";

    private void ShowMoment(Moment? m, bool pinned)
    {
        MomentBody.Children.Clear();
        MomentTotals.Inlines.Clear();
        MomentSpike.Visibility = Visibility.Collapsed;
        if (pinned && _pinned is DateTime p)
        {
            MomentTitle.Text = "At " + SpikeText.Time(m?.Utc ?? p);
            MomentSub.Text = PinnedSub(p);
        }
        else
        {
            MomentTitle.Text = "Now";
            MomentSub.Text = "Hover the graphs to look back. Click to pin a moment; scroll to move back in time.";
        }
        _shownMoment = m?.Utc ?? default;

        var at = m?.Utc ?? _pinned;
        SpikeInfo? spike = _pinnedSpike;
        if (spike == null && at is DateTime a)
            foreach (var s in _spikes)
                if (a >= s.StartUtc.AddSeconds(-0.5) && a <= s.EndUtc.AddSeconds(0.5)) { spike = s; break; }
        if (spike != null && (pinned || spike.Ongoing))
        {
            MomentSpike.Text = "⚠ " + SpikeText.Describe(spike) + (spike.Ongoing ? " – happening now" : "");
            MomentSpike.Visibility = Visibility.Visible;
        }

        if (m == null)
        {
            if (pinned)
            {
                MomentTotals.Inlines.Add(new Run("WinScope only keeps per-second details for the last 2 hours, so this moment's full breakdown has gone.") { Foreground = Palette.B(Palette.Muted) });
                if (_pinnedSpike is SpikeInfo ps && ps.Culprits.Length > 0)
                    Section("MAIN CULPRITS", ps.Culprits.Select(c => new AppValue(c.Name, c.Max)).ToArray(), SpikeText.KindColor(ps.Kind),
                        v => SpikeText.Value(ps.Kind, v), 0, "");
            }
            else MomentTotals.Inlines.Add(new Run("Collecting the first samples…") { Foreground = Palette.B(Palette.Muted) });
            return;
        }

        var t = m.Totals;
        Total("CPU", Format.Pct(t.Cpu), Palette.Accent);
        Total("GPU", double.IsNaN(t.Gpu) ? "—" : Format.Pct(t.Gpu) + (t.GpuOther >= 1 ? $" (+{Format.Pct(t.GpuOther)} other GPU)" : ""), Palette.Accent2);
        Total("RAM", Format.Pct(t.RamPct), Palette.Rx);
        Total("Disk", Format.Pct(t.Disk), Palette.Fair, last: true);

        Section("TOP CPU", m.TopCpu, Palette.Accent, v => AppRow.Pct(v), 20, "Nothing using noticeable CPU");
        Section("TOP GPU", m.TopGpu, Palette.Accent2, v => AppRow.Pct(v), 20,
            _graphs.Gpu == GpuStatus.Unavailable ? "GPU usage isn't available on this PC" : "Nothing using the GPU");
        Section("TOP MEMORY", m.TopRam, Palette.Rx, v => Format.Bytes(v), 0, "—");
        Section("TOP I/O  (disk, network and other)", m.TopIo, Palette.Fair, v => Format.Rate(v), 0, "No I/O this second");
    }

    private void Total(string label, string value, Color c, bool last = false)
    {
        MomentTotals.Inlines.Add(new Run(label + " ") { Foreground = Palette.B(Palette.Muted) });
        MomentTotals.Inlines.Add(new Run(value) { Foreground = Palette.B(c), FontWeight = FontWeights.SemiBold });
        if (!last) MomentTotals.Inlines.Add(new Run("   "));
    }

    /// <summary>A heading and up to 8 "name · value" rows, each with a thin bar relative to the top entry.</summary>
    private void Section(string title, AppValue[] apps, Color c, Func<double, string> fmt, double scaleFloor, string empty)
    {
        MomentBody.Children.Add(new TextBlock { Text = title, Style = (Style)FindResource("H"), Margin = new Thickness(0, 8, 0, 4) });
        if (apps.Length == 0)
        {
            MomentBody.Children.Add(new TextBlock { Text = empty, Foreground = Palette.B(Palette.Muted), FontSize = 12, Margin = new Thickness(0, 0, 0, 2) });
            return;
        }
        double max = Math.Max(scaleFloor, apps.Max(a => a.Value));
        var barColor = Color.FromArgb(200, c.R, c.G, c.B);
        foreach (var a in apps)
        {
            var g = new Grid { Margin = new Thickness(0, 1, 0, 3) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            g.RowDefinitions.Add(new RowDefinition { Height = new GridLength(4) });
            var name = new TextBlock { Text = a.Name, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12.5, ToolTip = a.Name };
            var val = new TextBlock { Text = fmt(a.Value), FontSize = 12.5, FontWeight = FontWeights.SemiBold, Foreground = Palette.B(c), Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(val, 1);
            var bar = new UsageBar { Value = max > 0 ? a.Value / max : 0, Fixed = barColor, Height = 3, Margin = new Thickness(0, 1, 0, 0) };
            Grid.SetRow(bar, 1);
            Grid.SetColumnSpan(bar, 2);
            g.Children.Add(name);
            g.Children.Add(val);
            g.Children.Add(bar);
            MomentBody.Children.Add(g);
        }
    }
}
