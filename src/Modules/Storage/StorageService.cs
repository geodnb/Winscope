using System.Windows.Threading;

namespace WinScope.Modules.Storage;

/// <summary>Background tasks that share one lifetime; Dispose cancels them and waits briefly.</summary>
internal sealed class TaskBag : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _tasks = new();
    private readonly object _lock = new();

    public CancellationToken Token => _cts.Token;

    public void Run(Func<CancellationToken, Task> work)
    {
        if (_cts.IsCancellationRequested) return;
        var ct = _cts.Token;
        var t = Task.Run(async () =>
        {
            try { await work(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception ex) { App.LogError(ex); }
        });
        lock (_lock)
        {
            _tasks.RemoveAll(x => x.IsCompleted);
            _tasks.Add(t);
        }
    }

    public void Run(Action<CancellationToken> work) => Run(ct => { work(ct); return Task.CompletedTask; });

    /// <summary>Tracked background computation; exceptions reach the awaiter. Null if already shutting down.</summary>
    public Task<T?> Compute<T>(Func<CancellationToken, T> work) where T : class
    {
        if (_cts.IsCancellationRequested) return Task.FromResult<T?>(null);
        var ct = _cts.Token;
        var t = Task.Run(() => (T?)work(ct));
        lock (_lock)
        {
            _tasks.RemoveAll(x => x.IsCompleted);
            _tasks.Add(t);
        }
        return t;
    }

    /// <summary>Tracked async work (e.g. a cleanup) – close/restart cancels it and waits for it briefly.</summary>
    public Task<T?> ComputeAsync<T>(Func<CancellationToken, Task<T>> work) where T : class
    {
        if (_cts.IsCancellationRequested) return Task.FromResult<T?>(null);
        var ct = _cts.Token;
        var t = Task.Run(async () => (T?)await work(ct).ConfigureAwait(false));
        lock (_lock)
        {
            _tasks.RemoveAll(x => x.IsCompleted);
            _tasks.Add(t);
        }
        return t;
    }

    /// <summary>Signal everything to stop now, without waiting.</summary>
    public void BeginStop()
    {
        try { _cts.Cancel(); } catch { }
    }

    public void Dispose()
    {
        BeginStop();
        Task[] all;
        lock (_lock) all = _tasks.ToArray();
        try { Task.WaitAll(all, 1000); } catch { }    // loops check the token per entry/file; never hang the close
    }
}

/// <summary>
/// Runs the latest posted action once on the UI thread, however many were posted in between – so background
/// work can report as often as it likes without flooding the dispatcher.
/// </summary>
internal sealed class UiPost
{
    private readonly Dispatcher _d;
    private Action? _latest;
    private int _queued;

    public UiPost(Dispatcher d) { _d = d; }

    public void Post(Action a)
    {
        Interlocked.Exchange(ref _latest, a);
        if (Interlocked.Exchange(ref _queued, 1) == 0)
        {
            try { _d.BeginInvoke(DispatcherPriority.Background, new Action(Run)); }
            catch { Interlocked.Exchange(ref _queued, 0); }
        }
    }

    private void Run()
    {
        Interlocked.Exchange(ref _queued, 0);
        var a = Interlocked.Exchange(ref _latest, null);
        try { a?.Invoke(); } catch (Exception ex) { App.LogError(ex); }
    }
}

internal sealed record DriveSnap(string Root, string Letter, string Label, string FileSystem, long Total, long Free, long Used, DriveType Type)
{
    public double UsedFraction => Total > 0 ? Math.Clamp(Used / (double)Total, 0, 1) : 0;
}

/// <summary>Drive list (every 30 s) and the cleanup engine. All work happens off the UI thread.</summary>
internal sealed class StorageService : IDisposable
{
    private readonly SemaphoreSlim _driveKick = new(0, 1);
    private volatile IReadOnlyList<DriveSnap> _drives = Array.Empty<DriveSnap>();
    private bool _started;

    public TaskBag Tasks { get; } = new();
    public CleanupEngine Cleanup { get; }
    public IReadOnlyList<DriveSnap> Drives => _drives;
    public bool DrivesLoaded { get; private set; }
    /// <summary>Raised on a background thread.</summary>
    public event Action? DrivesChanged;

    public StorageService() { Cleanup = new CleanupEngine(Tasks); }

    public void Start()
    {
        if (_started) return;
        _started = true;
        Tasks.Run(DriveLoop);
        Cleanup.Analyse();
    }

    /// <summary>Re-read the drives now (e.g. after cleaning) instead of waiting for the next 30 s tick.</summary>
    public void RefreshDrivesSoon()
    {
        try { if (_driveKick.CurrentCount == 0) _driveKick.Release(); } catch (SemaphoreFullException) { } catch (ObjectDisposedException) { }
    }

    private async Task DriveLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                _drives = ReadDrives();
                DrivesLoaded = true;
                DrivesChanged?.Invoke();
            }
            catch (Exception ex) { App.LogError(ex); }
            try { await _driveKick.WaitAsync(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
        }
    }

    private static IReadOnlyList<DriveSnap> ReadDrives()
    {
        var list = new List<DriveSnap>();
        string sysRoot = "";
        try { sysRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows)) ?? ""; } catch { }
        DriveInfo[] all;
        try { all = DriveInfo.GetDrives(); } catch (Exception ex) { App.LogError(ex); return list; }
        foreach (var d in all)
        {
            try
            {
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable) || !d.IsReady) continue;
                long total = d.TotalSize, free = d.AvailableFreeSpace, totalFree = d.TotalFreeSpace;
                string root = d.RootDirectory.FullName;
                string letter = root.TrimEnd('\\');
                string label = d.VolumeLabel;
                if (string.IsNullOrWhiteSpace(label)) label = d.DriveType == DriveType.Removable ? "USB drive" : "Local Disk";
                list.Add(new DriveSnap(root, letter, label, d.DriveFormat, total, free, Math.Max(0, total - totalFree), d.DriveType));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            catch (Exception ex) { App.LogError(ex); }
        }
        return list
            .OrderBy(x => string.Equals(x.Root, sysRoot, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenBy(x => x.Root, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public void BeginStop() => Tasks.BeginStop();

    public void Dispose()
    {
        Tasks.Dispose();
    }
}
