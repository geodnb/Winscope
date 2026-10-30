using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using WinScope.UI;

namespace WinScope.Modules.Events;

/// <summary>
/// Dashboard card body (446×245 at the minimum window, ~900×380 full HD): "N problems in the last 24 h", the
/// latest problems (as many rows as fit, up to 8), and last start / uptime. Plain TextBlocks only, so a click
/// anywhere opens the full view. The headline and the sentence under it count the same thing: every
/// non-harmless occurrence of Problem or Critical severity in the last 24 hours.
/// </summary>
internal sealed class EventsSummaryView : Grid
{
    private const double RowHeight = 24, ListHeadHeight = 31;
    private const int MaxRows = 8;
    private readonly TextBlock _headline = new() { FontSize = 20, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _sub = new() { FontSize = 12, Foreground = Palette.B(Palette.Muted), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 2, 0, 0) };
    private readonly TextBlock _listHead = new() { Text = "LATEST PROBLEMS", Margin = new Thickness(0, 12, 0, 4) };
    private readonly StackPanel _list = new();
    private readonly TextBlock _footer = new() { FontSize = 12, Foreground = Palette.B(Palette.Muted), TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };

    private IReadOnlyList<EventItemVm> _items = Array.Empty<EventItemVm>();
    private bool _loaded;
    private int _rowsFit = 4;

    public EventsSummaryView()
    {
        _listHead.SetResourceReference(StyleProperty, "H");
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Add(_headline, 0);
        Add(_sub, 1);
        Add(_listHead, 2);
        Add(_list, 3);
        Add(_footer, 4);
        ClipToBounds = true;
        _headline.Text = "Reading the event logs…";
        _headline.Foreground = Palette.B(Palette.Muted);
        _listHead.Visibility = Visibility.Collapsed;
        _footer.Text = EventsView.BootLine(null);
        SizeChanged += (_, _) => Fit();
    }

    private void Add(UIElement e, int row)
    {
        SetRow(e, row);
        Children.Add(e);
    }

    /// <summary>How many problem rows fit. Computed from the fixed parts' desired heights (not the list's own size),
    /// so hiding the list heading can't make it oscillate.</summary>
    private void Fit()
    {
        double fixedH = _headline.DesiredSize.Height + _sub.DesiredSize.Height + _footer.DesiredSize.Height + ListHeadHeight;
        int fit = (int)Math.Clamp(Math.Floor((ActualHeight - fixedH) / RowHeight), 0, MaxRows);
        if (fit == _rowsFit) return;
        _rowsFit = fit;
        if (_loaded) BuildRows();
    }

    public void Update(IReadOnlyList<EventItemVm> items, EventsSnapshot snap)
    {
        _items = items;
        _loaded = true;
        var since = DateTime.UtcNow - TimeSpan.FromHours(24);
        var kinds = EventAggregator.CountKinds(snap.Groups, since, EvSeverity.Problem);
        int problems = kinds.Sum(k => k.Count);
        bool serious = snap.Groups.Any(g => g.Items.Any(c => c.TimeUtc >= since && !c.Harmless && c.Severity == EvSeverity.Critical));

        if (problems == 0)
        {
            _headline.Text = "No problems in the last 24 h ✓";
            _headline.Foreground = Palette.B(Palette.Good);
            int minor = EventAggregator.CountKinds(snap.Groups, since).Sum(k => k.Count);
            _sub.Text = minor > 0 ? $"{minor} minor {(minor == 1 ? "error" : "errors")} logged – nothing that needs attention." : "Nothing worrying logged since this time yesterday.";
        }
        else
        {
            _headline.Text = problems == 1 ? "1 problem in the last 24 h" : $"{problems.ToString(CultureInfo.InvariantCulture)} problems in the last 24 h";
            _headline.Foreground = Palette.B(serious ? Palette.Bad : Palette.Weak);
            string s = EventAggregator.Sentence(kinds, 4);
            _sub.Text = s.Length > 0 ? char.ToUpperInvariant(s[0]) + s[1..] : "";
        }
        if (snap.Notes.FirstOrDefault(n => n.StartsWith("Couldn't read", StringComparison.Ordinal)) is string note)
            _sub.Text = note;

        _footer.Text = EventsView.BootLine(snap);
        BuildRows();
    }

    private void BuildRows()
    {
        _list.Children.Clear();
        _listHead.Visibility = _rowsFit > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_rowsFit == 0) return;
        int shown = 0;
        foreach (var vm in _items)
        {
            if (!Styles.IsProblem(vm.Newest)) continue;
            _list.Children.Add(Row(vm));
            if (++shown >= _rowsFit) break;
        }
        if (shown == 0)
            _list.Children.Add(new TextBlock { Text = "Nothing worrying in the last 30 days.", Foreground = Palette.B(Palette.Muted), Margin = new Thickness(0, 2, 0, 0) });
    }

    private static UIElement Row(EventItemVm vm)
    {
        var g = new Grid { Height = RowHeight };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(26) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var icon = new Border
        {
            Width = 18, Height = 18, CornerRadius = new CornerRadius(9), Background = vm.SevSoft, HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = vm.Glyph, Foreground = vm.SevBrush, FontSize = 11, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        };
        g.Children.Add(icon);
        // Title (not PlainTitle) so an "Other error" on the card gets its message line loaded.
        string title = vm.Title + (vm.Group.Count > 1 ? "  ×" + vm.Group.Count.ToString(CultureInfo.InvariantCulture) : "");
        var t = new TextBlock { Text = title, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center, ToolTip = vm.PlainTitle };
        SetColumn(t, 1);
        g.Children.Add(t);
        var ago = new TextBlock { Text = Format.Ago(vm.NewestLocal), Foreground = Palette.B(Palette.Muted), FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        SetColumn(ago, 2);
        g.Children.Add(ago);
        return g;
    }
}
