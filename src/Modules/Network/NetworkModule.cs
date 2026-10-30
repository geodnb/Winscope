using System.Windows;
using System.Windows.Threading;
using WinScope.Core;

namespace WinScope.Modules.Network;

/// <summary>
/// Network tool – a modern take on Resource Monitor's network tab: total speed with 30 minutes of history, which
/// apps have connections open and to where (Local / LAN / Internet), and – when running as administrator – how fast
/// each app is downloading and uploading (ETW, Microsoft-Windows-Kernel-Network).
/// </summary>
public sealed class NetworkModule : IToolModule
{
    public string Id => Sections.Network;
    public string Title => "Network";
    public string Tagline => "Which apps are using the internet";
    public string Icon => "⇅";

    private readonly NetworkMonitor _monitor;
    private readonly NetworkFullView _full;
    private readonly NetworkSummaryView _summary;
    private readonly Dispatcher _dispatcher;
    private int _uiPending;
    private volatile bool _summaryVisible, _fullVisible;   // read by the monitor thread in OnUpdated
    private bool _started;
    private volatile bool _disposed;

    public FrameworkElement Summary => _summary;
    public FrameworkElement FullView => _full;

    // The summary has no buttons of its own; clicking the card opens the full view (handled by the shell).
    public event Action? OpenRequested { add { } remove { } }

    public NetworkModule()
    {
        NetLog.Sink = App.LogError;
        _monitor = new NetworkMonitor(Elevation.IsAdmin);
        _full = new NetworkFullView(_monitor.History);
        _summary = new NetworkSummaryView();
        _dispatcher = _full.Dispatcher;
        _monitor.Updated += OnUpdated;
    }

    public void Start()
    {
        if (_started || _disposed) return;
        _started = true;
        // If WinScope dies without Dispose (crash), don't leave the ETW session running in the kernel.
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
        _monitor.Start();
    }

    public void SetVisibility(bool summaryVisible, bool fullViewVisible)
    {
        _summaryVisible = summaryVisible;
        _fullVisible = fullViewVisible;
        _full.SetActive(fullViewVisible);   // false while minimised too (the shell folds that in)
        if (summaryVisible || fullViewVisible) Apply();   // show the latest data straight away, not on the next tick
    }

    /// <summary>
    /// Background → UI, coalesced: at most one pending dispatcher item at any time, and none at all while neither
    /// view is on screen (collection carries on; SetVisibility shows the latest snapshot as soon as one is).
    /// </summary>
    private void OnUpdated()
    {
        if (_disposed || !(_summaryVisible || _fullVisible)) return;
        if (Interlocked.Exchange(ref _uiPending, 1) == 1) return;
        try
        {
            _dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            {
                Interlocked.Exchange(ref _uiPending, 0);
                Apply();
            });
        }
        catch { Interlocked.Exchange(ref _uiPending, 0); }
    }

    private void Apply()
    {
        if (_disposed) return;
        var s = _monitor.Latest;
        if (s == null) return;
        try
        {
            if (_summaryVisible) _summary.Update(s, _monitor.History);
            if (_fullVisible) _full.Update(s);
        }
        catch (Exception ex) { NetLog.Error(ex); }
    }

    private static void StopOrphanSession()
    {
        try { if (EtwNetworkSession.OwnsSessionName) EtwNetworkSession.StopByName(); } catch { }
    }

    private static void OnProcessExit(object? sender, EventArgs e) => StopOrphanSession();
    private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e) { if (e.IsTerminating) StopOrphanSession(); }

    /// <summary>Closing: stop the loop and the ETW session now; Dispose then waits at most ~0.9 s.</summary>
    public void BeginStop()
    {
        _monitor.Updated -= OnUpdated;
        try { _full.SetActive(false); } catch { }
        try { _monitor.BeginStop(); }
        catch (Exception ex) { NetLog.Error(ex); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _monitor.Updated -= OnUpdated;
        try { _monitor.Dispose(); }
        catch (Exception ex) { NetLog.Error(ex); }
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        AppDomain.CurrentDomain.UnhandledException -= OnUnhandled;
    }
}
