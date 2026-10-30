using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using WinScope.Core;
using WinScope.UI;

namespace WinScope.Modules.Network;

/// <summary>Shared bits for building the views in code.</summary>
internal static class Ui
{
    public static Style Res(string key) => (Style)Application.Current.FindResource(key);
    public static readonly Brush Muted = Palette.B(Palette.Muted);
    public static readonly Brush Text = Palette.B(Palette.Text);
    public static FontFamily Mono => (FontFamily)Application.Current.FindResource("MonoFont");

    public static TextBlock Label(string text, Color? c = null, double size = 11) => new()
    {
        Text = text, FontSize = size, FontWeight = FontWeights.SemiBold,
        Foreground = c is Color col ? Palette.B(col) : Muted
    };

    public static TextBlock Small(string text = "") => new()
    {
        Text = text, FontSize = 11.5, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis
    };

    /// <summary>Text column with a TextBlock element style (alignment, tooltip, colour, font).</summary>
    public static DataGridTextColumn Col(object header, string path, double width, string? sortPath = null, bool right = false,
        string? tipPath = null, bool mono = false, string? brushPath = null, double minWidth = 0)
    {
        var st = new Style(typeof(TextBlock));
        st.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
        if (right) st.Setters.Add(new Setter(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Right));
        if (tipPath != null) st.Setters.Add(new Setter(FrameworkElement.ToolTipProperty, new Binding(tipPath)));
        if (mono) { st.Setters.Add(new Setter(TextBlock.FontFamilyProperty, Mono)); st.Setters.Add(new Setter(TextBlock.FontSizeProperty, 12.0)); }
        if (brushPath != null) st.Setters.Add(new Setter(TextBlock.ForegroundProperty, new Binding(brushPath)));
        var col = new DataGridTextColumn
        {
            Header = header,
            Binding = new Binding(path) { Mode = BindingMode.OneWay },
            SortMemberPath = sortPath ?? path,
            ElementStyle = st,
            Width = width < 0 ? new DataGridLength(-width, DataGridLengthUnitType.Star) : new DataGridLength(width),
            MinWidth = minWidth > 0 ? minWidth : 20,
        };
        return col;
    }

    public static TextBlock Header(string text, string tip) => new() { Text = text, ToolTip = tip };
}

/// <summary>The Network tool's full view: totals + graph on top, apps on the left, the selected app's connections on the right.</summary>
internal sealed class NetworkFullView : Grid
{
    private const string AdminText = "Per-app speeds need administrator rights – Windows only exposes them to admin tools. Connections are shown without it.";
    private const int MaxConnRows = 2000;

    private readonly NetGraph _graph;
    private readonly TextBlock _downNow = BigValue(NetGraph.DownColor), _upNow = BigValue(NetGraph.UpColor);
    private readonly TextBlock _session = Ui.Small(), _adapters = Ui.Small(), _countText = Ui.Small();
    private readonly TextBlock _connHeader = new() { FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _appsEmpty = EmptyText(), _connsEmpty = EmptyText();
    private readonly Border _warnStrip;
    private readonly TextBlock _warnText = new() { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Text };
    private readonly TextBox _filter = new() { VerticalContentAlignment = VerticalAlignment.Center };
    private readonly DataGrid _apps, _conns;
    private readonly DataGridColumn _colDown, _colUp, _colTotal, _colConnApp;
    private readonly Button _showAll = new() { Content = "Show all apps", Padding = new Thickness(8, 2, 8, 2), FontSize = 11.5, Visibility = Visibility.Collapsed };

    private readonly ObservableCollection<AppRowVm> _appRows = new();
    private readonly Dictionary<string, AppRowVm> _appByKey = new(StringComparer.OrdinalIgnoreCase);
    private ObservableCollection<ConnRowVm> _connRows = new();
    private readonly Dictionary<string, ConnRowVm> _connByKey = new();
    private readonly ListCollectionView _appView;
    private ListCollectionView _connView;

    private NetSnapshot? _last;
    private string? _selectedKey;
    private bool _listeningOnly;
    private bool? _perAppShown;
    private bool _userSorted;
    private int _connVersionShown = -1;
    private bool _connDirty = true;
    private bool _updatingApps;

    private static TextBlock BigValue(Color c) => new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Palette.B(c), Text = "—" };
    private static TextBlock EmptyText() => new()
    {
        Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(20, 40, 20, 0), IsHitTestVisible = false
    };

    public NetworkFullView(RateHistory history)
    {
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                          // 0 notices
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(2, GridUnitType.Star), MinHeight = 170, MaxHeight = 320 }); // 1 graph
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                                          // 3 toolbar
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(8) });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(3, GridUnitType.Star), MinHeight = 170 });    // 5 tables

        // ---- notices
        var notices = new StackPanel();
        var admin = new AdminNotice { Section = Sections.Network, Message = AdminText, Margin = new Thickness(0, 0, 0, 10) };
        notices.Children.Add(admin);
        _warnStrip = new Border
        {
            Background = Palette.B(Palette.Fair, 0.08), BorderBrush = Palette.B(Palette.Fair, 0.4), BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8), Padding = new Thickness(12, 8, 12, 8), Margin = new Thickness(0, 0, 0, 10),
            Child = _warnText, Visibility = Visibility.Collapsed
        };
        notices.Children.Add(_warnStrip);
        Children.Add(notices);

        // ---- top: totals + graph
        var top = new Border { Style = Ui.Res("Card"), Padding = new Thickness(16, 12, 12, 8) };
        SetRow(top, 1);
        var tg = new Grid();
        tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(200) });
        tg.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel { Margin = new Thickness(0, 2, 12, 0) };
        left.Children.Add(Ui.Label("↓  DOWNLOAD", NetGraph.DownColor));
        left.Children.Add(_downNow);
        var upLabel = Ui.Label("↑  UPLOAD", NetGraph.UpColor);
        upLabel.Margin = new Thickness(0, 6, 0, 0);
        left.Children.Add(upLabel);
        left.Children.Add(_upNow);
        _session.Margin = new Thickness(0, 8, 0, 0);
        left.Children.Add(_session);
        left.Children.Add(_adapters);
        _adapters.ToolTip = "Real network adapters being counted. Virtual adapters, VPN tunnels and loopback are left out so nothing is counted twice.";
        tg.Children.Add(left);

        var right = new DockPanel();
        SetColumn(right, 1);
        var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
        DockPanel.SetDock(head, Dock.Top);
        var chips = new StackPanel { Orientation = Orientation.Horizontal };
        DockPanel.SetDock(chips, Dock.Right);
        foreach (var (label, secs) in new[] { ("5 min", 300), ("15 min", 900), ("30 min", 1800) })
        {
            var rb = new RadioButton { Content = label, GroupName = "net-window", Style = Ui.Res("ChipRadio"), IsChecked = secs == 300, FontSize = 11.5, Padding = new Thickness(8, 2, 8, 2) };
            rb.Checked += (_, _) => { _graph!.WindowSeconds = secs; _graph.MarkDirty(); };
            chips.Children.Add(rb);
        }
        head.Children.Add(chips);
        var legend = new TextBlock { FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Muted };
        legend.Inlines.Add(new System.Windows.Documents.Run("━ ") { Foreground = Palette.B(NetGraph.DownColor) });
        legend.Inlines.Add(new System.Windows.Documents.Run("Download    "));
        legend.Inlines.Add(new System.Windows.Documents.Run("━ ") { Foreground = Palette.B(NetGraph.UpColor) });
        legend.Inlines.Add(new System.Windows.Documents.Run("Upload"));
        head.Children.Add(legend);
        right.Children.Add(head);
        _graph = new NetGraph { History = history, WindowSeconds = 300 };
        right.Children.Add(_graph);
        tg.Children.Add(right);
        top.Child = tg;
        Children.Add(top);

        // ---- toolbar: filter + view chips + counts
        var bar = new DockPanel();
        SetRow(bar, 3);
        _countText.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(_countText, Dock.Right);
        bar.Children.Add(_countText);
        var filterHost = new Grid { Width = 280, Margin = new Thickness(0, 0, 12, 0) };
        filterHost.Children.Add(_filter);
        var hint = new TextBlock
        {
            Text = "Filter by app, address or port", Foreground = Ui.Muted, IsHitTestVisible = false,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(11, 0, 0, 0)
        };
        filterHost.Children.Add(hint);
        _filter.TextChanged += (_, _) =>
        {
            hint.Visibility = _filter.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            Refilter();
        };
        DockPanel.SetDock(filterHost, Dock.Left);
        bar.Children.Add(filterHost);
        var modes = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        var all = new RadioButton { Content = "All apps", GroupName = "net-mode", Style = Ui.Res("ChipRadio"), IsChecked = true };
        var listen = new RadioButton
        {
            Content = "Listening ports", GroupName = "net-mode", Style = Ui.Res("ChipRadio"),
            ToolTip = "Apps waiting for incoming connections. \"Any network\" means other devices can reach it if the firewall allows."
        };
        all.Checked += (_, _) => { _listeningOnly = false; Refilter(); };
        listen.Checked += (_, _) => { _listeningOnly = true; Refilter(); };
        modes.Children.Add(all);
        modes.Children.Add(listen);
        bar.Children.Add(modes);
        Children.Add(bar);

        // ---- tables
        var tables = new Grid();
        SetRow(tables, 5);
        tables.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(11, GridUnitType.Star), MinWidth = 380 });
        tables.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
        tables.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(9, GridUnitType.Star), MinWidth = 340 });

        _apps = new DataGrid { SelectionMode = DataGridSelectionMode.Single, CanUserReorderColumns = false };
        _colDown = Ui.Col("↓ Down", nameof(AppRowVm.DownText), 84, nameof(AppRowVm.Down), right: true);
        _colUp = Ui.Col("↑ Up", nameof(AppRowVm.UpText), 80, nameof(AppRowVm.Up), right: true);
        _colTotal = Ui.Col(Ui.Header("Total", "Downloaded + uploaded since WinScope started"), nameof(AppRowVm.TotalText), 76, nameof(AppRowVm.Total), right: true, tipPath: nameof(AppRowVm.TotalTip));
        _apps.Columns.Add(Ui.Col("App", nameof(AppRowVm.NameText), -1, nameof(AppRowVm.Name), tipPath: nameof(AppRowVm.Tip), minWidth: 110));
        _apps.Columns.Add(_colDown);
        _apps.Columns.Add(_colUp);
        _apps.Columns.Add(_colTotal);
        _apps.Columns.Add(Ui.Col(Ui.Header("TCP", "Established TCP connections"), nameof(AppRowVm.Established), 50, right: true));
        _apps.Columns.Add(Ui.Col(Ui.Header("Listen", "Ports this app is listening on for incoming connections"), nameof(AppRowVm.ListeningPorts), 56, right: true));
        _apps.Columns.Add(Ui.Col(Ui.Header("UDP", "Open UDP endpoints (UDP has no connections, just open ports)"), nameof(AppRowVm.Udp), 50, right: true));
        _apps.ItemsSource = _appRows;
        _appView = (ListCollectionView)CollectionViewSource.GetDefaultView(_appRows);
        _appView.IsLiveSorting = true;
        foreach (var p in new[] { nameof(AppRowVm.TotalRate), nameof(AppRowVm.Total), nameof(AppRowVm.ConnSort), nameof(AppRowVm.Name),
                     nameof(AppRowVm.Down), nameof(AppRowVm.Up), nameof(AppRowVm.Established), nameof(AppRowVm.ListeningPorts), nameof(AppRowVm.Udp) })
            _appView.LiveSortingProperties.Add(p);
        _apps.Sorting += (_, _) => _userSorted = true;
        _apps.SelectionChanged += (_, _) =>
        {
            if (_updatingApps && _apps.SelectedItem == null && _selectedKey != null && _appByKey.ContainsKey(_selectedKey)) return;
            var key = (_apps.SelectedItem as AppRowVm)?.Key;
            if (key == _selectedKey) return;
            _selectedKey = key;
            _connDirty = true;
            if (_last != null) UpdateConns(_last);
        };
        ApplyDefaultSort(Elevation.IsAdmin);
        var appsCard = new Border { Style = Ui.Res("Card"), Padding = new Thickness(2, 4, 2, 2) };
        var ag = new Grid();
        ag.Children.Add(_apps);
        ag.Children.Add(_appsEmpty);
        appsCard.Child = ag;
        tables.Children.Add(appsCard);

        _conns = new DataGrid { SelectionMode = DataGridSelectionMode.Extended, CanUserReorderColumns = false };
        _colConnApp = Ui.Col("App", nameof(ConnRowVm.App), -0.8, minWidth: 80);
        _conns.Columns.Add(_colConnApp);
        _conns.Columns.Add(Ui.Col("Proto", nameof(ConnRowVm.Proto), 50));
        _conns.Columns.Add(Ui.Col(Ui.Header("Port", "Port on this PC"), nameof(ConnRowVm.LocalPort), 58, right: true));
        _conns.Columns.Add(Ui.Col("Remote address", nameof(ConnRowVm.Remote), -1, nameof(ConnRowVm.RemoteSort), tipPath: nameof(ConnRowVm.Tip), mono: true, minWidth: 110));
        _conns.Columns.Add(Ui.Col("State", nameof(ConnRowVm.State), 80, nameof(ConnRowVm.StateOrder)));
        _conns.Columns.Add(Ui.Col(Ui.Header("Where", "Local = this PC · LAN = your home/office network · Shared/VPN = carrier-grade NAT or a VPN like Tailscale · Internet = everything else"),
            nameof(ConnRowVm.WhereText), 82, tipPath: nameof(ConnRowVm.WhereTip), brushPath: nameof(ConnRowVm.WhereBrush)));
        _conns.ItemsSource = _connRows;
        _connView = (ListCollectionView)CollectionViewSource.GetDefaultView(_connRows);
        _connView.SortDescriptions.Add(new SortDescription(nameof(ConnRowVm.StateOrder), ListSortDirection.Ascending));
        _connView.SortDescriptions.Add(new SortDescription(nameof(ConnRowVm.RemoteSort), ListSortDirection.Ascending));
        _connView.SortDescriptions.Add(new SortDescription(nameof(ConnRowVm.LocalPort), ListSortDirection.Ascending));

        var connCard = new Border { Style = Ui.Res("Card"), Padding = new Thickness(2, 4, 2, 2) };
        SetColumn(connCard, 2);
        var cd = new DockPanel();
        var ch = new DockPanel { Margin = new Thickness(10, 4, 6, 6) };
        DockPanel.SetDock(ch, Dock.Top);
        DockPanel.SetDock(_showAll, Dock.Right);
        _showAll.Click += (_, _) => _apps.SelectedItem = null;
        ch.Children.Add(_showAll);
        ch.Children.Add(_connHeader);
        cd.Children.Add(ch);
        var cg = new Grid();
        cg.Children.Add(_conns);
        cg.Children.Add(_connsEmpty);
        cd.Children.Add(cg);
        connCard.Child = cd;
        tables.Children.Add(connCard);
        Children.Add(tables);

        _appsEmpty.Text = "Reading connections…";
    }

    /// <summary>On screen (from the module's SetVisibility): runs the graph's redraw timer only then.</summary>
    public void SetActive(bool active) => _graph.Active = active;

    private void ApplyDefaultSort(bool perApp)
    {
        _appView.SortDescriptions.Clear();
        foreach (var c in _apps.Columns) c.SortDirection = null;
        if (perApp)
        {
            _appView.SortDescriptions.Add(new SortDescription(nameof(AppRowVm.TotalRate), ListSortDirection.Descending));
            _appView.SortDescriptions.Add(new SortDescription(nameof(AppRowVm.Total), ListSortDirection.Descending));
        }
        _appView.SortDescriptions.Add(new SortDescription(nameof(AppRowVm.ConnSort), ListSortDirection.Descending));
        _appView.SortDescriptions.Add(new SortDescription(nameof(AppRowVm.Name), ListSortDirection.Ascending));
    }

    private void Refilter()
    {
        _connDirty = true;
        if (_last != null) Update(_last);
    }

    public void Update(NetSnapshot s)
    {
        _last = s;
        _downNow.Text = Format.Rate(s.RxRate);
        _upNow.Text = Format.Rate(s.TxRate);
        _session.Text = $"This session  ↓ {Format.Bytes(s.SessionRx)}  ↑ {Format.Bytes(s.SessionTx)}";
        _adapters.Text = s.AdapterNames.Length > 0 ? "Counting: " + s.AdapterNames : "No connected network adapter";

        if (_perAppShown != s.PerApp)
        {
            _perAppShown = s.PerApp;
            var v = s.PerApp ? Visibility.Visible : Visibility.Collapsed;
            _colDown.Visibility = _colUp.Visibility = _colTotal.Visibility = v;
            if (!_userSorted) ApplyDefaultSort(s.PerApp);
        }

        string? warn = s.EtwMessage;
        if (s.ConnError != null) warn = (warn != null ? warn + "\n" : "") + "Couldn't read all connections (" + s.ConnError + ").";
        _warnText.Text = warn ?? "";
        _warnStrip.Visibility = warn != null ? Visibility.Visible : Visibility.Collapsed;

        UpdateApps(s);
        if (_connDirty || s.ConnVersion != _connVersionShown) UpdateConns(s);
        _countText.Text = $"{_appRows.Count} app{(_appRows.Count == 1 ? "" : "s")} · {s.ConnCount} connections and endpoints";
    }

    // ------------------------------------------------------------------ filtering

    private (string? text, int? port) FilterTerms()
    {
        string t = _filter.Text.Trim();
        if (t.Length == 0) return (null, null);
        string p = t.TrimStart(':');
        int? port = int.TryParse(p, out int n) && n is > 0 and <= 65535 ? n : null;
        return (t, port);
    }

    private static bool AppNameMatches(AppStat a, string t) =>
        a.Name.Contains(t, StringComparison.OrdinalIgnoreCase) || (a.Path?.Contains(t, StringComparison.OrdinalIgnoreCase) ?? false);

    private static bool ConnMatches(ConnInfo c, string t, int? port) =>
        (port is int p && (c.LocalPort == p || (c.HasRemote && c.RemotePort == p))) ||
        (c.HasRemote && c.RemoteText.Contains(t, StringComparison.OrdinalIgnoreCase));

    private bool AppPasses(AppStat a, string? t, int? port)
    {
        if (_listeningOnly && a.ListeningPorts == 0) return false;
        if (t == null || AppNameMatches(a, t)) return true;
        foreach (var c in a.Conns)
            if ((!_listeningOnly || c.IsListening) && ConnMatches(c, t, port)) return true;
        return false;
    }

    // ------------------------------------------------------------------ apps table

    private void UpdateApps(NetSnapshot s)
    {
        var (t, port) = FilterTerms();
        var wanted = new Dictionary<string, AppStat>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in s.Apps) if (AppPasses(a, t, port)) wanted[a.Key] = a;

        _updatingApps = true;
        try
        {
            for (int i = _appRows.Count - 1; i >= 0; i--)
            {
                var r = _appRows[i];
                if (wanted.ContainsKey(r.Key)) continue;
                _appRows.RemoveAt(i);
                _appByKey.Remove(r.Key);
            }
            foreach (var (k, a) in wanted)
            {
                if (_appByKey.TryGetValue(k, out var vm)) { vm.Update(a); continue; }
                vm = new AppRowVm(k);
                vm.Update(a);
                _appByKey[k] = vm;
                _appRows.Add(vm);
            }
        }
        finally { _updatingApps = false; }

        // If the selected app went away (closed, or filtered out), fall back to "all apps".
        if (_selectedKey != null && !_appByKey.ContainsKey(_selectedKey))
        {
            _selectedKey = null;
            _connDirty = true;
        }
        _appsEmpty.Text = _appRows.Count > 0 ? "" :
            t != null ? "No apps match the filter." :
            _listeningOnly ? "No apps are listening for incoming connections." : "No apps are using the network right now.";
    }

    // ------------------------------------------------------------------ connections table

    /// <summary>
    /// Big change (another app selected, a burst of new connections): swap in a new collection and sort once, rather
    /// than hundreds of sorted inserts on the UI thread. The DataGrid clears sorting when ItemsSource changes, so
    /// the current sort (and the header arrows) are carried over.
    /// </summary>
    private void ReplaceConnRows(List<ConnRowVm> list)
    {
        var sorts = _connView.SortDescriptions.ToArray();
        var dirs = _conns.Columns.Select(c => c.SortDirection).ToArray();
        _connByKey.Clear();
        foreach (var r in list) _connByKey[r.Key] = r;
        _connRows = new ObservableCollection<ConnRowVm>(list);
        _conns.ItemsSource = _connRows;
        _connView = (ListCollectionView)CollectionViewSource.GetDefaultView(_connRows);
        using (_connView.DeferRefresh())
        {
            _connView.SortDescriptions.Clear();
            foreach (var sd in sorts) _connView.SortDescriptions.Add(sd);
        }
        for (int i = 0; i < dirs.Length && i < _conns.Columns.Count; i++) _conns.Columns[i].SortDirection = dirs[i];
    }

    private void UpdateConns(NetSnapshot s)
    {
        _connDirty = false;
        _connVersionShown = s.ConnVersion;
        var (t, port) = FilterTerms();
        var list = new List<ConnRowVm>();
        var keys = new HashSet<string>();
        bool truncated = false;
        AppStat? selected = null;
        foreach (var a in s.Apps)
        {
            if (_selectedKey != null && !a.Key.Equals(_selectedKey, StringComparison.OrdinalIgnoreCase)) continue;
            if (_selectedKey != null) selected = a;
            if (_selectedKey == null && !AppPasses(a, t, port)) continue;
            bool nameMatch = t == null || AppNameMatches(a, t);
            foreach (var c in a.Conns)
            {
                if (_listeningOnly && !c.IsListening) continue;
                if (!nameMatch && !ConnMatches(c, t!, port)) continue;
                if (list.Count >= MaxConnRows) { truncated = true; break; }
                string key = c.Key + "|" + a.Key;
                if (!keys.Add(key)) continue;
                if (_connByKey.TryGetValue(key, out var existing)) list.Add(existing);
                else list.Add(new ConnRowVm(c, a.Name));
            }
            if (truncated) break;
        }

        int changes = 0;
        foreach (var r in _connRows) if (!keys.Contains(r.Key)) changes++;
        foreach (var r in list) if (!_connByKey.ContainsKey(r.Key)) changes++;
        if (changes > 150) ReplaceConnRows(list);
        else
        {
            // Small change: diff into the bound collection (keeps scroll position and selected rows).
            for (int i = _connRows.Count - 1; i >= 0; i--)
            {
                if (keys.Contains(_connRows[i].Key)) continue;
                _connByKey.Remove(_connRows[i].Key);
                _connRows.RemoveAt(i);
            }
            foreach (var r in list)
            {
                if (_connByKey.ContainsKey(r.Key)) continue;
                _connByKey[r.Key] = r;
                _connRows.Add(r);
            }
        }

        bool one = _selectedKey != null;
        _colConnApp.Visibility = one ? Visibility.Collapsed : Visibility.Visible;
        _showAll.Visibility = one ? Visibility.Visible : Visibility.Collapsed;
        string what = _listeningOnly ? "listening port" : "connection";
        string count = $"{_connRows.Count}{(truncated ? "+" : "")} {what}{(_connRows.Count == 1 ? "" : "s")}";
        _connHeader.Text = one ? $"{selected?.Name ?? _selectedKey} – {count}" : $"All apps – {count}";
        _connHeader.ToolTip = one ? null : "Select an app on the left to see only its connections.";
        _connsEmpty.Text = _connRows.Count > 0 ? "" :
            one ? (_listeningOnly ? "This app isn't listening on any port." : "This app has no open connections right now.") :
            t != null ? "No connections match the filter." : "No connections.";
    }
}
/// <summary>
/// Dashboard card: ↓/↑ now with a 2-minute sparkline, the top apps and the admin hint. Fits the minimum card body
/// (about 446 × 245) with 3 apps; on bigger cards (about 900 × 380) the sparkline grows and up to 5 apps show.
/// Rows that don't fit are hidden (worked out on SizeChanged), so nothing is ever clipped.
/// </summary>
internal sealed class NetworkSummaryView : Grid
{
    private const int SparkSeconds = 120, MaxRows = SummaryLayout.MaxRows;
    private const double RowH = SummaryLayout.RowH, SparkGap = SummaryLayout.SparkGap;
    private readonly TextBlock _down = Big(NetGraph.DownColor), _up = Big(NetGraph.UpColor);
    private readonly Sparkline _sDown = new() { Stroke = NetGraph.DownColor, Min = 0, FixedMax = true };
    private readonly Sparkline _sUp = new() { Stroke = NetGraph.UpColor, Min = 0, FixedMax = true };
    private readonly RowDefinition _sparkRow = new() { Height = new GridLength(56) };
    private readonly TextBlock _topHeader = Ui.Label("TOP APPS");
    private readonly TextBlock _empty = Ui.Small();
    private readonly TextBlock _hint = Ui.Small();
    private readonly (Grid row, TextBlock name, UsageBar bar, TextBlock value)[] _rows = new (Grid, TextBlock, UsageBar, TextBlock)[MaxRows];
    private readonly double[] _rx = new double[SparkSeconds], _tx = new double[SparkSeconds];
    private int _rowCap = 3;
    private bool _showBars = true;
    private NetSnapshot? _last;
    private RateHistory? _history;

    private static TextBlock Big(Color c) => new() { FontSize = 22, FontWeight = FontWeights.SemiBold, Foreground = Palette.B(c), Text = "—" };

    public NetworkSummaryView()
    {
        ClipToBounds = true;
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });            // numbers
        RowDefinitions.Add(_sparkRow);                                                 // sparkline
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });            // top header
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });            // rows
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });            // hint

        var nums = new Grid();
        nums.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nums.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nums.Children.Add(Stack(Ui.Label("↓  DOWNLOAD", NetGraph.DownColor), _down));
        var upStack = Stack(Ui.Label("↑  UPLOAD", NetGraph.UpColor), _up);
        SetColumn(upStack, 1);
        nums.Children.Add(upStack);
        Children.Add(nums);

        var spark = new Grid { Margin = new Thickness(0, SparkGap, 0, 0) };
        SetRow(spark, 1);
        spark.Children.Add(_sDown);
        spark.Children.Add(_sUp);
        spark.Children.Add(new TextBlock
        {
            Text = "last 2 min", FontSize = 10.5, Foreground = Ui.Muted, HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top, IsHitTestVisible = false
        });
        Children.Add(spark);

        _topHeader.Margin = new Thickness(0, 10, 0, 4);
        SetRow(_topHeader, 2);
        Children.Add(_topHeader);

        var rows = new StackPanel();
        SetRow(rows, 3);
        for (int i = 0; i < _rows.Length; i++)
        {
            var g = new Grid { Height = RowH };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Foreground = Ui.Text, Margin = new Thickness(0, 0, 10, 0) };
            var bar = new UsageBar { Fixed = NetGraph.DownColor, VerticalAlignment = VerticalAlignment.Center };
            SetColumn(bar, 1);
            var val = new TextBlock { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Foreground = Ui.Muted, FontSize = 12, Margin = new Thickness(10, 0, 0, 0) };
            SetColumn(val, 2);
            g.Children.Add(name); g.Children.Add(bar); g.Children.Add(val);
            g.Visibility = Visibility.Collapsed;
            rows.Children.Add(g);
            _rows[i] = (g, name, bar, val);
        }
        rows.Children.Add(_empty);
        Children.Add(rows);

        _hint.Margin = new Thickness(0, 6, 0, 0);
        SetRow(_hint, 5);
        Children.Add(_hint);

        SizeChanged += (_, _) => { Relayout(); Render(); };
    }

    private static StackPanel Stack(params UIElement[] items)
    {
        var s = new StackPanel();
        foreach (var i in items) s.Children.Add(i);
        return s;
    }

    private void Relayout()
    {
        if (ActualHeight <= 0) return;
        var (rows, spark) = SummaryLayout.Fit(ActualHeight, _hint.Visibility == Visibility.Visible);
        _rowCap = rows;
        _sparkRow.Height = new GridLength(spark);
        _showBars = ActualWidth >= 400;
    }

    public void Update(NetSnapshot s, RateHistory history)
    {
        _last = s; _history = history;
        bool hintWas = _hint.Visibility == Visibility.Visible;
        string hint = !Elevation.IsAdmin ? "Per-app speeds: restart as admin"
            : !s.PerApp && s.EtwMessage != null ? "Per-app speeds are unavailable – open Network for details"
            : "";
        _hint.Text = hint;
        _hint.Visibility = hint.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        if (hintWas != (hint.Length > 0)) Relayout();
        Render();
    }

    private void Render()
    {
        var s = _last;
        if (s == null || _history == null) return;
        _down.Text = Format.Rate(s.RxRate);
        _up.Text = Format.Rate(s.TxRate);

        int n = _history.CopyLast(SparkSeconds, _rx, _tx);
        var rx = new double[n]; var tx = new double[n];
        Array.Copy(_rx, rx, n); Array.Copy(_tx, tx, n);
        double mx = 16 * 1024;
        for (int i = 0; i < n; i++) mx = Math.Max(mx, Math.Max(rx[i], tx[i]));
        _sDown.Max = _sUp.Max = mx * 1.1;
        _sDown.SetPoints(rx);
        _sUp.SetPoints(tx);

        bool bySpeed = s.PerApp && s.Apps.Any(a => a.Down + a.Up >= 1024);
        var top = bySpeed
            ? s.Apps.Where(a => a.Down + a.Up >= 1).OrderByDescending(a => a.Down + a.Up).Take(_rowCap).ToList()
            : s.Apps.Where(a => a.Established > 0).OrderByDescending(a => a.Established).ThenBy(a => a.Name).Take(_rowCap).ToList();
        _topHeader.Text = bySpeed ? "TOP APPS BY SPEED" : "TOP APPS BY ACTIVE CONNECTIONS";
        _topHeader.Visibility = _rowCap > 0 ? Visibility.Visible : Visibility.Collapsed;
        double best = top.Count == 0 ? 1 : bySpeed ? Math.Max(1, top[0].Down + top[0].Up) : Math.Max(1, top[0].Established);
        for (int i = 0; i < _rows.Length; i++)
        {
            var (row, name, bar, val) = _rows[i];
            if (i >= top.Count) { row.Visibility = Visibility.Collapsed; continue; }
            var a = top[i];
            row.Visibility = Visibility.Visible;
            bar.Visibility = _showBars ? Visibility.Visible : Visibility.Collapsed;
            name.Text = a.ProcCount > 1 ? $"{a.Name}  ×{a.ProcCount}" : a.Name;
            name.ToolTip = a.Path;
            if (bySpeed)
            {
                bar.Value = (a.Down + a.Up) / best;
                val.Text = $"↓ {Format.Rate(a.Down)}   ↑ {Format.Rate(a.Up)}";
            }
            else
            {
                bar.Value = a.Established / best;
                val.Text = a.Established == 1 ? "1 connection" : $"{a.Established} connections";
            }
        }
        bool showEmpty = top.Count == 0 && _rowCap > 0;
        _empty.Text = showEmpty ? "Nothing is connected right now." : "";
        _empty.Visibility = showEmpty ? Visibility.Visible : Visibility.Collapsed;
    }
}
