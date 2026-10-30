using System.Windows;
using Microsoft.Win32;
using System.Windows.Threading;
using WinScope.Core;

namespace WinScope.Modules.Events;

/// <summary>
/// Events tool: pulls the events that matter (crashes, blue screens, driver resets, hardware errors, Wi-Fi drops,
/// failed updates, unexpected shutdowns…) out of the System and Application logs and explains each in plain English.
/// Reading needs no admin rights. Collection runs in <see cref="EventsEngine"/>; this class marshals its snapshots to
/// the UI thread (coalesced: at most one pending dispatch of each kind) and only rebuilds views that are visible.
/// </summary>
public sealed class EventsModule : IToolModule
{
    private readonly EventsEngine _engine = new();
    private readonly EventsView _view;
    private readonly EventsSummaryView _summary;
    private readonly Dispatcher _ui;

    private Dictionary<string, EventItemVm> _byIdentity = new(StringComparer.Ordinal);
    private Dictionary<string, EventItemVm> _byMessageKey = new(StringComparer.Ordinal);
    private List<EventItemVm> _items = new();
    private EventsSnapshot? _snap;
    private volatile EventsSnapshot? _pending;
    private int _posted, _msgPosted;
    private bool _summaryVisible, _fullVisible, _summaryDirty, _fullDirty, _started, _disposed;

    public EventsModule()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _view = new EventsView();
        _summary = new EventsSummaryView();
        _view.RefreshRequested += () => _engine.Kick();
        _engine.Updated += OnUpdated;
        _engine.MessagesReady += OnMessagesReady;
    }

    public string Id => Sections.Events;
    public string Title => "Events";
    public string Tagline => "What Windows has been logging, in plain English";
    public string Icon => "☰";
    public FrameworkElement Summary => _summary;
    public FrameworkElement FullView => _view;

    // Never raised: the summary has no buttons, and the card itself opens the full view on click.
    public event Action? OpenRequested { add { } remove { } }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        // Time zone or clock changes: clear .NET's cached zone and regroup by the new local days.
        SystemEvents.TimeChanged += OnTimeChanged;
        _engine.Start();
    }

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        try
        {
            TimeZoneInfo.ClearCachedData();
            _engine.TimeZoneChanged();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    public void BeginStop() => _engine.BeginStop();

    public void SetVisibility(bool summaryVisible, bool fullViewVisible)
    {
        _summaryVisible = summaryVisible;
        _fullVisible = fullViewVisible;
        RefreshVisible();
    }

    // ------------------------------------------------------------------ snapshots (background → UI)

    private void OnUpdated(EventsSnapshot snap)
    {
        _pending = snap;
        if (Interlocked.Exchange(ref _posted, 1) == 0)
            _ui.BeginInvoke(DispatcherPriority.Background, new Action(ApplyPending));
    }

    private void ApplyPending()
    {
        Interlocked.Exchange(ref _posted, 0);
        if (_disposed) return;
        var snap = _pending;
        if (snap == null) return;
        try
        {
            if (_snap == null || !ReferenceEquals(snap.Groups, _snap.Groups)) Map(snap.Groups);
            _snap = snap;
            _summaryDirty = _fullDirty = true;
            RefreshVisible();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>Groups → view models, keeping the same VM for the same group so expanded state and bindings survive.</summary>
    private void Map(IReadOnlyList<EventGroup> groups)
    {
        var byId = new Dictionary<string, EventItemVm>(groups.Count, StringComparer.Ordinal);
        var byMsg = new Dictionary<string, EventItemVm>(groups.Count, StringComparer.Ordinal);
        var list = new List<EventItemVm>(groups.Count);
        foreach (var g in groups)
        {
            if (_byIdentity.TryGetValue(g.Identity, out var vm)) vm.Update(g);
            else vm = new EventItemVm(g, _engine);
            byId[g.Identity] = vm;
            byMsg[vm.MessageKey] = vm;
            list.Add(vm);
        }
        _byIdentity = byId;
        _byMessageKey = byMsg;
        _items = list;
    }

    private void RefreshVisible()
    {
        if (_disposed || _snap == null) return;
        try
        {
            if (_summaryVisible && _summaryDirty) { _summary.Update(_items, _snap); _summaryDirty = false; }
            if (_fullVisible && _fullDirty) { _view.Update(_items, _snap); _fullDirty = false; }
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    // ------------------------------------------------------------------ lazily formatted messages

    private void OnMessagesReady()
    {
        if (Interlocked.Exchange(ref _msgPosted, 1) == 0)
            _ui.BeginInvoke(DispatcherPriority.Background, new Action(DrainMessages));
    }

    private void DrainMessages()
    {
        Interlocked.Exchange(ref _msgPosted, 0);
        if (_disposed) return;
        try
        {
            int n = 0;
            while (n++ < 500 && _engine.DequeueReady() is string key)
                if (_byMessageKey.TryGetValue(key, out var vm)) vm.OnMessageReady();
            if (n > 500) OnMessagesReady();      // more queued: continue in a later dispatcher turn
            else _summaryDirty = true;           // dashboard titles of "Other errors" may have changed
            if (_summaryVisible) RefreshVisible();
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_started) SystemEvents.TimeChanged -= OnTimeChanged;   // static event: must unhook
        _engine.Updated -= OnUpdated;
        _engine.MessagesReady -= OnMessagesReady;
        _engine.Dispose();
    }
}
