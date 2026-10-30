using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using WinScope.UI;

namespace WinScope.Modules.Events;

/// <summary>
/// Full Events view: summary sentence, range / category / search filters, and a virtualised day-grouped timeline.
/// Filtering runs on the UI thread over already-aggregated groups (cheap: a few thousand items at most).
/// </summary>
public partial class EventsView : UserControl
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly EvCategory?[] CategoryOrder =
    {
        null, EvCategory.Crashes, EvCategory.Graphics, EvCategory.Hardware, EvCategory.Network,
        EvCategory.Updates, EvCategory.Power, EvCategory.Services, EvCategory.Other,
    };

    private readonly EventsPrefs _prefs = EventsPrefs.Load();
    private readonly Dictionary<DateTime, DayHeaderVm> _days = new();
    private readonly Dictionary<int, RadioButton> _chips = new();     // key: -1 = All, else (int)category
    private readonly DispatcherTimer _searchTimer;
    private ObservableCollection<object> _rows = new();
    private IReadOnlyList<EventItemVm> _items = Array.Empty<EventItemVm>();
    private EventsSnapshot? _snap;
    private EvCategory? _cat;
    private string _query = "";
    private bool _init;

    internal event Action? RefreshRequested;

    public EventsView()
    {
        InitializeComponent();
        List.ItemsSource = _rows;
        Admin.Section = Core.Sections.Events;
        BuildChips();
        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(250) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            string q = SearchBox.Text.Trim().ToLowerInvariant();
            if (q == _query) return;
            _query = q;
            Rebuild(scrollTop: true);
        };
        (_prefs.RangeDays switch { 1 => R1, 30 => R30, _ => R7 }).IsChecked = true;
        InfoBox.IsChecked = _prefs.ShowInfo;
        HarmlessBox.IsChecked = _prefs.ShowHarmless;
        InfoBox.Content = "Show info events";
        HarmlessBox.Content = "Show harmless warnings";
        _init = true;
        ShowLoading();
    }

    private void BuildChips()
    {
        foreach (var c in CategoryOrder)
        {
            var rb = new RadioButton
            {
                GroupName = "evcat",
                Style = (Style)FindResource("ChipRadio"),
                Margin = new Thickness(0, 0, 6, 6),
                Content = c == null ? "All" : EventItemVm.CategoryName(c.Value),
                IsChecked = c == null,
            };
            var cat = c;
            rb.Checked += (_, _) =>
            {
                if (!_init) return;
                _cat = cat;
                Rebuild(scrollTop: true);
            };
            _chips[c == null ? -1 : (int)c.Value] = rb;
            CatPanel.Children.Add(rb);
        }
    }

    private void ShowLoading()
    {
        SummaryText.Inlines.Clear();
        SummaryText.Inlines.Add(new Run("Reading the event logs…") { Foreground = Palette.B(Palette.Muted) });
        EmptyText.Text = "Reading the System and Application logs…";
        EmptyText.Visibility = Visibility.Visible;
    }

    /// <summary>New data from the module (UI thread).</summary>
    internal void Update(IReadOnlyList<EventItemVm> items, EventsSnapshot snap)
    {
        _items = items;
        _snap = snap;
        foreach (var d in _days.Values) d.RefreshText();
        UpdatedText.Text = "Updated " + snap.TakenUtc.ToLocalTime().ToString("HH:mm", Inv);
        RefreshBtn.IsEnabled = true;
        Rebuild(scrollTop: false);
    }

    private TimeSpan Range => TimeSpan.FromDays(_prefs.RangeDays);

    private string RangeLabel => _prefs.RangeDays switch { 1 => "Last 24 hours", 30 => "Last 30 days", _ => "Last 7 days" };

    private void Rebuild(bool scrollTop)
    {
        if (_snap == null) { ShowLoading(); return; }
        var nowUtc = DateTime.UtcNow;
        var since = nowUtc - Range;
        int hiddenInfo = 0, hiddenHarmless = 0, all = 0;
        var counts = new int[Enum.GetValues<EvCategory>().Length];
        var rows = new List<object>(Math.Min(_items.Count * 2, 20000));
        var used = new HashSet<DateTime>();
        DayHeaderVm? header = null;
        int dayCount = 0;

        foreach (var vm in _items)
        {
            if (vm.NewestUtc < since) break;                  // items are newest first
            var c = vm.Newest;
            if (c.Harmless && !_prefs.ShowHarmless) { hiddenHarmless++; continue; }
            if (!c.Harmless && c.Severity == EvSeverity.Info && !_prefs.ShowInfo) { hiddenInfo++; continue; }
            if (_query.Length > 0 && !vm.Matches(_query)) continue;
            counts[(int)c.Category]++;
            all++;
            if (_cat is EvCategory k && c.Category != k) continue;
            var day = vm.NewestLocal.Date;
            if (header == null || header.Date != day)
            {
                if (header != null) header.CountText = EventsLabel(dayCount);
                if (!_days.TryGetValue(day, out header)) _days[day] = header = new DayHeaderVm(day);
                used.Add(day);
                rows.Add(header);
                dayCount = 0;
            }
            rows.Add(vm);
            dayCount += vm.Group.Count;
        }
        if (header != null) header.CountText = EventsLabel(dayCount);
        foreach (var d in _days.Keys.Where(d => !used.Contains(d)).ToList()) _days.Remove(d);

        ApplyRows(rows, scrollTop);

        // Chips and toggles
        foreach (var (key, rb) in _chips)
        {
            string name = key == -1 ? "All" : EventItemVm.CategoryName((EvCategory)key);
            int n = key == -1 ? all : counts[key];
            rb.Content = n > 0 ? $"{name}  {n.ToString(Inv)}" : name;
            rb.Opacity = n > 0 || key == -1 || rb.IsChecked == true ? 1.0 : 0.55;
        }
        InfoBox.Content = !_prefs.ShowInfo && hiddenInfo > 0 ? $"Show info events ({hiddenInfo.ToString(Inv)})" : "Show info events";
        HarmlessBox.Content = !_prefs.ShowHarmless && hiddenHarmless > 0 ? $"Show harmless warnings ({hiddenHarmless.ToString(Inv)})" : "Show harmless warnings";

        UpdateSummary(since);
        UpdateNotes(since);

        if (rows.Count == 0)
        {
            EmptyText.Text = _query.Length > 0 ? $"Nothing matches “{SearchBox.Text.Trim()}” in this period."
                : _cat != null ? $"Nothing in {EventItemVm.CategoryName(_cat.Value)} for this period."
                : "Nothing worrying in this period ✓" + (hiddenInfo > 0 && !_prefs.ShowInfo ? "\nTurn on ‘Show info events’ to see restarts, sleep, wake-ups and installed updates." : "");
            EmptyText.Visibility = Visibility.Visible;
        }
        else EmptyText.Visibility = Visibility.Collapsed;
    }

    private static string EventsLabel(int n) => n == 1 ? "1 event" : n.ToString(Inv) + " events";

    /// <summary>Minimal in-place update (common prefix/suffix) so a new event doesn't reset the scroll position.</summary>
    private void ApplyRows(List<object> rows, bool scrollTop)
    {
        var cur = _rows;
        int max = Math.Min(cur.Count, rows.Count), p = 0, s = 0;
        while (p < max && ReferenceEquals(cur[p], rows[p])) p++;
        while (s < max - p && ReferenceEquals(cur[cur.Count - 1 - s], rows[rows.Count - 1 - s])) s++;
        int remove = cur.Count - p - s, insert = rows.Count - p - s;
        if (remove != 0 || insert != 0)
        {
            if (remove + insert > 200)
            {
                _rows = new ObservableCollection<object>(rows);
                List.ItemsSource = _rows;
            }
            else
            {
                for (int i = 0; i < remove; i++) cur.RemoveAt(p);
                for (int i = 0; i < insert; i++) cur.Insert(p + i, rows[p + i]);
            }
        }
        if (scrollTop && _rows.Count > 0) List.ScrollIntoView(_rows[0]);
    }

    private void UpdateSummary(DateTime sinceUtc)
    {
        if (_snap == null) return;
        var kinds = EventAggregator.CountKinds(_snap.Groups, sinceUtc);
        string sentence = EventAggregator.Sentence(kinds);
        SummaryText.Inlines.Clear();
        SummaryText.Inlines.Add(new Run(RangeLabel + ": ") { Foreground = Palette.B(Palette.Muted) });
        // Red only when something in range is actually Critical (a one-off USB card-reader error isn't).
        bool serious = _snap.Groups.Any(g => g.Items.Any(c => c.TimeUtc >= sinceUtc && !c.Harmless && c.Severity == EvSeverity.Critical));
        bool problems = EventAggregator.CountKinds(_snap.Groups, sinceUtc, EvSeverity.Problem).Count > 0;
        if (sentence.Length == 0)
            SummaryText.Inlines.Add(new Run("no problems found ✓") { Foreground = Palette.B(Palette.Good), FontWeight = FontWeights.SemiBold });
        else
            SummaryText.Inlines.Add(new Run(sentence)
            {
                FontWeight = FontWeights.SemiBold,
                Foreground = Palette.B(serious ? Palette.Bad : problems ? Palette.Text : Palette.Muted),
            });
        SummaryText.Inlines.Add(new Run("  ·  " + BootLine(_snap)) { Foreground = Palette.B(Palette.Muted), FontSize = 13 });
    }

    /// <summary>
    /// "Last restart: Tue 08:12 · Up 2d 4h", or with Fast Startup "Last start: Wed 07:55 (Fast Startup) · Last full
    /// restart: Sun 19:02". The tick count only resets on a full start (Fast Startup resumes the kernel), so the most
    /// recent start comes from Kernel-Boot 27 in the log when it is later than the tick-count boot time.
    /// </summary>
    internal static string BootLine(EventsSnapshot? snap)
    {
        var now = DateTime.Now;
        var up = TimeSpan.FromMilliseconds(Environment.TickCount64);
        var fullBoot = now - up;
        if (snap?.LastStartUtc is DateTime ls && snap.LastStartKind is 1 or 2)
        {
            var start = ls.ToLocalTime();
            if (start > fullBoot + TimeSpan.FromMinutes(3) && start <= now)
                return $"Last start: {When(start)} ({(snap.LastStartKind == 1 ? "Fast Startup" : "resumed from hibernation")})  ·  Last full restart: {When(fullBoot)}";
        }
        return $"Last restart: {When(fullBoot)}  ·  Up {Format.Span(up)}";
    }

    private static string When(DateTime local)
    {
        var now = DateTime.Now;
        if (local.Date == now.Date) return "today " + local.ToString("HH:mm", Inv);
        return (now - local).TotalDays < 6 ? local.ToString("ddd HH:mm", Inv) : local.ToString("d MMM HH:mm", Inv);
    }

    private void UpdateNotes(DateTime sinceUtc)
    {
        var lines = new List<string>();
        if (_snap != null)
        {
            foreach (var n in _snap.Notes)
            {
                // Only mention a read cap if it actually cuts into the chosen range.
                if (n.Contains("only the newest", StringComparison.Ordinal) && !(_snap.CompleteSinceUtc > sinceUtc)) continue;
                lines.Add(n);
            }
        }
        if (_prefs.ShowHarmless)
            lines.Add("Harmless warnings are things Windows logs on almost every PC – DCOM permission warnings, time-sync retries and the like – that Microsoft says are safe to ignore. They're shown faded.");
        bool denied = _snap?.Notes.Any(n => n.Contains("access was denied", StringComparison.Ordinal)) == true;
        Admin.Visibility = denied && !Core.Elevation.IsAdmin ? Visibility.Visible : Visibility.Collapsed;
        NoteText.Text = string.Join("\n", lines);
        NoteBox.Visibility = lines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ handlers

    private void Range_Checked(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        _prefs.RangeDays = sender == R1 ? 1 : sender == R30 ? 30 : 7;
        _prefs.Save();
        Rebuild(scrollTop: true);
    }

    private void Toggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_init) return;
        _prefs.ShowInfo = InfoBox.IsChecked == true;
        _prefs.ShowHarmless = HarmlessBox.IsChecked == true;
        _prefs.Save();
        Rebuild(scrollTop: false);
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e)
    {
        SearchHint.Visibility = SearchBox.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (!_init) return;
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        UpdatedText.Text = "Checking…";
        RefreshBtn.IsEnabled = false;
        try { RefreshRequested?.Invoke(); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not EventItemVm vm || sender is not Button b) return;
        string result;
        try { Clipboard.SetText(vm.CopyText()); result = "Copied ✓"; }
        catch (ExternalException) { result = "Clipboard busy"; }   // another app has the clipboard open
        b.Content = result;
        // Rows are recycled, so put the label back shortly rather than leaving it on someone else's item.
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
        t.Tick += (_, _) => { t.Stop(); b.Content = "Copy"; };
        t.Start();
    }

    /// <summary>The read-only details box would otherwise swallow the mouse wheel; pass it on to the list.</summary>
    private void Details_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not DependencyObject d) return;
        e.Handled = true;
        if (VisualTreeHelper.GetParent(d) is UIElement parent)
            parent.RaiseEvent(new MouseWheelEventArgs(e.MouseDevice, e.Timestamp, e.Delta) { RoutedEvent = UIElement.MouseWheelEvent, Source = sender });
    }
}
