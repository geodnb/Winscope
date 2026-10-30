using System.Windows;
using WinScope.Core;

namespace WinScope.Modules.Storage;

/// <summary>
/// Storage: drives, a space analyser and safe cleanup ("preview, then clean"). Cleanup categories delete only
/// true temp/cache files, permanently, after a confirmation and a per-file re-check; the user's own files only
/// ever go to the Recycle Bin, one at a time, from the analyser. The dashboard card never deletes anything.
/// </summary>
public sealed class StorageModule : IToolModule
{
    private readonly StorageService _svc;
    private readonly StorageSummary _summary;
    private readonly StorageView _full;
    private bool _disposed;

    public string Id => Sections.Storage;
    public string Title => "Storage";
    public string Tagline => "What's filling your drives, and safe cleanup";
    public string Icon => "▤";
    public FrameworkElement Summary => _summary;
    public FrameworkElement FullView => _full;
    public event Action? OpenRequested;

    public StorageModule()
    {
        _svc = new StorageService();
        _summary = new StorageSummary(_svc);
        _full = new StorageView(_svc);
        _summary.ReviewRequested += () =>
        {
            try { _full.ShowCleanup(); OpenRequested?.Invoke(); }
            catch (Exception ex) { App.LogError(ex); }
        };
    }

    public void Start() => _svc.Start();

    /// <summary>Non-null while a restart would interrupt something.</summary>
    public string? BusyReason
    {
        get { try { return _full.BusyReason; } catch { return null; } }
    }

    /// <summary>Signal every scan, analysis, clean and timer to stop now; Dispose then waits at most ~1 s.</summary>
    public void BeginStop()
    {
        try { _full.Stop(); } catch (Exception ex) { App.LogError(ex); }
        _svc.BeginStop();
    }

    public void SetVisibility(bool summaryVisible, bool fullViewVisible)
    {
        _summary.SetVisible(summaryVisible);
        _full.SetVisible(fullViewVisible);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _full.Stop(); } catch (Exception ex) { App.LogError(ex); }
        _svc.Dispose();
    }
}
