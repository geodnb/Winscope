using System.Windows;
using System.Windows.Threading;
using WinScope.Core;

namespace WinScope.Modules.Performance;

/// <summary>
/// Performance: Task Manager only shows "right now"; this records the last two hours, second by second, so you can
/// answer "what was hogging my PC when my game stuttered 5 minutes ago?". Sampling runs in the background from
/// <see cref="Start"/> until <see cref="Dispose"/>, whether or not anything is on screen; the views refresh once
/// a second only while visible.
/// </summary>
public sealed class PerformanceModule : IToolModule
{
    private readonly Sampler _sampler = new();
    private readonly PerformanceView _view;
    private readonly PerformanceSummary _summary;
    private readonly Dispatcher _ui;
    private volatile bool _summaryVisible, _fullVisible, _disposed;
    private int _pending;
    private int _viewSpikeVersion = -1;
    private SpikeInfo[] _spikes = Array.Empty<SpikeInfo>();
    private int _spikesVersion = -1;

    public string Id => Sections.Performance;
    public string Title => "Performance";
    public string Tagline => "What's been using your PC";
    public string Icon => "↗";
    public FrameworkElement Summary => _summary;
    public FrameworkElement FullView => _view;

    // The summary has no buttons of its own; clicking the card opens the full view (the shell does that).
    public event Action? OpenRequested { add { } remove { } }

    public PerformanceModule()
    {
        _ui = Dispatcher.CurrentDispatcher;
        _view = new PerformanceView(_sampler);
        _summary = new PerformanceSummary();
        _sampler.Sampled += OnSampled;
    }

    public void Start()
    {
        _sampler.Start();
        // A time-zone or clock change: drop the cached zone so labels use the new one straight away.
        try { Microsoft.Win32.SystemEvents.TimeChanged += OnTimeChanged; _timeHooked = true; }
        catch (Exception ex) { App.LogError(ex); }
    }

    private bool _timeHooked;

    private void OnTimeChanged(object? sender, EventArgs e)
    {
        try { TimeZoneInfo.ClearCachedData(); } catch { }
        OnSampled();   // coalesced UI refresh (relabels the graphs and lists)
    }

    public void SetVisibility(bool summaryVisible, bool fullViewVisible)
    {
        _summaryVisible = summaryVisible;
        _fullVisible = fullViewVisible;
        _view.SetActive(fullViewVisible);
        if (summaryVisible || fullViewVisible) Refresh();
    }

    /// <summary>Sampler thread: queue at most one UI refresh at a time, and none while nothing is visible.</summary>
    private void OnSampled()
    {
        if (_disposed || (!_summaryVisible && !_fullVisible)) return;
        if (Interlocked.Exchange(ref _pending, 1) == 1) return;
        try { _ui.BeginInvoke(DispatcherPriority.Background, Refresh); }
        catch { Interlocked.Exchange(ref _pending, 0); }
    }

    private void Refresh()
    {
        Interlocked.Exchange(ref _pending, 0);
        if (_disposed) return;
        try
        {
            var d = _sampler.Latest;
            int v = _sampler.Spikes.Version;
            if (v != _spikesVersion) { _spikes = _sampler.Spikes.Snapshot(); _spikesVersion = v; }
            if (_fullVisible)
            {
                _view.Refresh(d, _viewSpikeVersion != _spikesVersion ? _spikes : null);
                _viewSpikeVersion = _spikesVersion;
            }
            if (_summaryVisible)
            {
                _summary.Refresh(d, _sampler.History, _spikes.Length > 0 ? _spikes[0] : null);
            }
        }
        catch (Exception ex) { App.LogError(ex); }
    }

    /// <summary>
    /// The Performance part of a WinScope report, from the history and spike log already in memory (one pass over
    /// up to 2 h of samples under the history lock – a few milliseconds). Never throws.
    /// </summary>
    public ReportSection? BuildReport(ReportOptions options)
    {
        try
        {
            var input = new PerfReportInput
            {
                NowStamp = PerfClock.Now,
                ToLocal = PerfClock.ToLocal,
                Window = _sampler.History.Summarize(DateTime.MinValue),
                Latest = _sampler.Latest,
                Spikes = _sampler.Spikes.Snapshot(),
                Redact = options.Redact ? Redactor.Apply : null,
            };
            var (findings, body) = PerfReport.Build(input);
            return new ReportSection { Title = Title, Findings = findings, Body = body };
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return new ReportSection { Title = Title, Body = "Not available right now (the performance figures couldn't be summarised)." };
        }
    }

    /// <summary>Shutdown, step 1: stop sampling and timers now, without waiting.</summary>
    public void BeginStop()
    {
        if (_disposed) return;
        _disposed = true;
        _sampler.Sampled -= OnSampled;
        if (_timeHooked) { try { Microsoft.Win32.SystemEvents.TimeChanged -= OnTimeChanged; } catch { } _timeHooked = false; }
        try { _view.SetActive(false); } catch { }
        _sampler.BeginStop();
    }

    /// <summary>Shutdown, step 2: waits at most ~0.9 s for the sampler loop to release its native resources.</summary>
    public void Dispose()
    {
        BeginStop();
        _sampler.Dispose();
    }
}
