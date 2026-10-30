using System.Diagnostics;

namespace WinScope.Modules.Network;

/// <summary>
/// Background collection for the Network tool: one loop on a 1 s PeriodicTimer that
///  • reads the adapter byte counters (total ↓/↑, 30 min history),
///  • every 2nd tick re-reads the TCP/UDP tables (connections per app),
///  • drains the ETW per-PID byte counters when running as admin,
/// and publishes an immutable <see cref="NetSnapshot"/>. It never touches the UI; <see cref="Updated"/> is raised on
/// the loop's thread and the module coalesces it onto the dispatcher.
/// </summary>
internal sealed class NetworkMonitor : IDisposable
{
    public RateHistory History { get; } = new(30 * 60 + 120);
    public NetSnapshot? Latest => _latest;
    public event Action? Updated;

    private volatile NetSnapshot? _latest;
    private readonly AdapterCounters _adapters = new();
    private readonly ProcessNames _names = new();
    private readonly bool _admin;
    private CancellationTokenSource? _cts;
    private Task? _loop;

    // ETW (admin only)
    private EtwNetworkSession? _etw;          // swapped with Interlocked (the loop may restart it while Dispose runs)
    private volatile bool _disposed;
    private Mutex? _etwMutex;
    private bool _etwBlockedByOtherInstance;
    private int _etwRestarts;
    private long _etwFailedAt;
    private long _rxSinceEtwStart;
    private readonly Dictionary<uint, (long down, long up)> _pidBytes = new();

    // loop state
    private readonly List<ConnInfo> _conns = new();
    private string? _connError;
    private int _connVersion;
    private long _tick;
    private long _lastTs;
    private long _sessionRx, _sessionTx;
    private readonly Dictionary<string, (long down, long up)> _appSession = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, (long down, long up)> _appPrevTick = new(StringComparer.OrdinalIgnoreCase);
    private double _prevDt;
    private DateTime _sessionStart;
    /// <summary>ETW bytes for PIDs whose name wasn't known yet, merged into the real app once it resolves.</summary>
    private readonly Dictionary<uint, Pending> _pending = new();
    private sealed class Pending { public long Down, Up; public long FirstFileTime; public long FirstTick; }

    public NetworkMonitor(bool admin) => _admin = admin;

    /// <summary>Called on the UI thread (the ETW mutex is owned by this thread and released in Dispose on it).</summary>
    public void Start()
    {
        if (_cts != null) return;
        if (_admin) StartEtw(first: true);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => Loop(ct));
    }

    private void StartEtw(bool first)
    {
        if (first)
        {
            // Only one WinScope may own the "WinScope-Network" session at a time; without this a second elevated
            // window would stop the first one's session when it clears out what it thinks is a crashed run.
            try
            {
                _etwMutex = new Mutex(false, @"Global\WinScope-Network-ETW");
                bool owned;
                try { owned = _etwMutex.WaitOne(0); }
                catch (AbandonedMutexException) { owned = true; }   // previous owner crashed: ours now
                if (!owned) { _etwMutex.Dispose(); _etwMutex = null; _etwBlockedByOtherInstance = true; return; }
            }
            catch (Exception ex)
            {
                // Couldn't create/open it – most likely another user's elevated copy owns it with an ACL we can't
                // open. Don't touch the session at all in that case (starting would stop theirs).
                NetLog.Error(ex);
                _etwMutex?.Dispose();
                _etwMutex = null;
                _etwBlockedByOtherInstance = true;
                return;
            }
        }
        if (_disposed) return;
        try
        {
            var e = new EtwNetworkSession();
            Interlocked.Exchange(ref _etw, e);   // full fence: the _disposed read below can't move above this write
            Interlocked.Exchange(ref _rxSinceEtwStart, 0);
            if (_disposed) { Interlocked.Exchange(ref _etw, null)?.Dispose(); return; }   // lost a race with Dispose
            e.Start();
        }
        catch (Exception ex) { NetLog.Error(ex); }
    }

    private async Task Loop(CancellationToken ct)
    {
        _lastTs = Stopwatch.GetTimestamp();
        _sessionStart = MonoClock.UtcNow;
        try { _adapters.ReadDelta(); } catch (Exception ex) { NetLog.Error(ex); }   // baseline
        try { Tick(ct, first: true); } catch (Exception ex) { NetLog.Error(ex); }
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { Tick(ct, first: false); }
                catch (Exception ex) { NetLog.Error(ex); }
            }
        }
        catch (OperationCanceledException) { }
    }

    private void Tick(CancellationToken ct, bool first)
    {
        if (ct.IsCancellationRequested) return;
        long ts = Stopwatch.GetTimestamp();
        double dt = Math.Max(0.05, (ts - _lastTs) / (double)Stopwatch.Frequency);
        _lastTs = ts;
        var now = MonoClock.UtcNow;   // monotonic: a wall-clock step back can't freeze the graph
        _tick++;

        // 1. Totals across the physical adapters.
        double rxRate = 0, txRate = 0;
        if (!first)
        {
            var (rx, tx) = _adapters.ReadDelta();
            if (dt > 10) { rx = 0; tx = 0; }   // resumed from sleep: don't draw one huge spike
            rxRate = rx / dt; txRate = tx / dt;
            _sessionRx += rx; _sessionTx += tx;
            Interlocked.Add(ref _rxSinceEtwStart, rx + tx);
            History.Add(now, rxRate, txRate);
        }

        // 2. Connections every 2 s; the process list as needed.
        _names.Tick();
        if (first || _tick % 2 == 0)
        {
            _connError = ConnectionTables.ReadAll(_conns);
            _connVersion++;
        }

        // 3. Per-app bytes (admin + ETW).
        var etw = Volatile.Read(ref _etw);
        bool perApp = etw != null && etw.State == EtwState.Running;
        if (etw != null) etw.Drain(_pidBytes); else _pidBytes.Clear();
        string? etwMessage = EtwStatus(etw, ct);

        // 4. Group by app.
        var acc = new Dictionary<string, Acc>(StringComparer.OrdinalIgnoreCase);
        Acc For(uint pid)
        {
            var info = _names.Get(pid);
            if (!acc.TryGetValue(info.Name, out var a)) acc[info.Name] = a = new Acc(info.Name, info.Path) { Placeholder = info.Placeholder };
            a.Pids.Add(pid);
            a.Path ??= info.Path;
            return a;
        }
        foreach (var c in _conns)
        {
            var a = For(c.Pid);
            a.Conns.Add(c);
            if (c.Proto == Proto.Udp) a.Udp++;
            else
            {
                a.TcpAll++;
                if (c.State == TcpState.Established) a.Established++;
                else if (c.State == TcpState.Listen) a.ListenPorts.Add(c.LocalPort);
            }
        }
        foreach (var (pid, (d, u)) in _pidBytes)
        {
            var a = For(pid);
            a.TickDown += d; a.TickUp += u;
        }

        // Session totals and smoothed (2 s) rates per app.
        var thisTick = new Dictionary<string, (long, long)>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in acc.Values)
        {
            if (a.TickDown == 0 && a.TickUp == 0) continue;
            thisTick[a.Name] = (a.TickDown, a.TickUp);
            if (a.Placeholder)
            {
                // Name not known yet ("Process 1234" is one PID): park the bytes by PID rather than starting a
                // separate app total that would outlive the name lookup.
                foreach (var pid in a.Pids)
                {
                    if (!_pidBytes.TryGetValue(pid, out var b) || (b.down == 0 && b.up == 0)) continue;
                    if (!_pending.TryGetValue(pid, out var pe))
                        _pending[pid] = pe = new Pending { FirstFileTime = DateTime.UtcNow.ToFileTimeUtc(), FirstTick = Environment.TickCount64 };
                    pe.Down += b.down; pe.Up += b.up;
                }
                continue;
            }
            AddSession(a.Name, a.TickDown, a.TickUp);
        }
        ResolvePending();
        var apps = new List<AppStat>(acc.Count + 8);
        foreach (var a in acc.Values) apps.Add(Build(a, dt));
        if (perApp || _appSession.Count > 0)
        {
            // Apps that moved data earlier this session but have nothing open now stay listed (≥ 64 KB).
            foreach (var (name, (d, u)) in _appSession)
            {
                if (acc.ContainsKey(name) || d + u < 64 * 1024) continue;
                apps.Add(new AppStat { Key = name, Name = name, TotalDown = d, TotalUp = u, Conns = Array.Empty<ConnInfo>() });
            }
        }
        _appPrevTick = thisTick;
        _prevDt = dt;

        _latest = new NetSnapshot
        {
            Utc = now,
            RxRate = rxRate, TxRate = txRate,
            SessionRx = _sessionRx, SessionTx = _sessionTx,
            Apps = apps,
            ConnVersion = _connVersion,
            ConnCount = _conns.Count,
            PerApp = perApp,
            EtwMessage = etwMessage,
            ConnError = _connError,
            AdapterNames = _adapters.AdapterNames + (_adapters.UsingFallback ? " (basic mode)" : ""),
            Adapters = _adapters.Adapters,
            SessionStartUtc = _sessionStart,
            PerAppSinceUtc = perApp ? etw!.StartedUtc : null,
        };
        try { Updated?.Invoke(); } catch (Exception ex) { NetLog.Error(ex); }
    }

    private void AddSession(string name, long down, long up)
    {
        _appSession.TryGetValue(name, out var s);
        _appSession[name] = (s.down + down, s.up + up);
    }

    /// <summary>
    /// Move parked bytes to the app once its PID resolves. Keyed by PID + time: if the process that now has the PID
    /// started after the bytes were seen, the PID was reused, so they go to "Process N (ended)" instead.
    /// </summary>
    private void ResolvePending()
    {
        if (_pending.Count == 0) return;
        List<uint>? done = null;
        foreach (var (pid, pe) in _pending)
        {
            var info = _names.Get(pid);
            string target;
            if (info.Placeholder)
            {
                if (Environment.TickCount64 - pe.FirstTick < 30_000) continue;   // keep waiting for the process list
                target = $"Process {pid} (ended)";
            }
            else if (info.StartFileTime > 0 && info.StartFileTime > pe.FirstFileTime + 2 * TimeSpan.TicksPerSecond)
                target = $"Process {pid} (ended)";
            else target = info.Name;
            AddSession(target, pe.Down, pe.Up);
            (done ??= new()).Add(pid);
        }
        if (done != null) foreach (var pid in done) _pending.Remove(pid);
        if (_pending.Count > 5000) _pending.Clear();   // defensive cap
    }

    private AppStat Build(Acc a, double dt)
    {
        // Rate over the last two ticks: steadier numbers, and a burst fades over 2 s instead of blinking.
        bool hadPrev = _appPrevTick.TryGetValue(a.Name, out var prev);
        double span = hadPrev ? dt + _prevDt : dt;
        _appSession.TryGetValue(a.Name, out var total);
        var pids = a.Pids.Count <= 12 ? string.Join(", ", a.Pids.Order()) : string.Join(", ", a.Pids.Order().Take(12)) + $" +{a.Pids.Count - 12}";
        return new AppStat
        {
            Key = a.Name,
            Name = a.Name,
            Path = a.Path,
            ProcCount = a.Pids.Count,
            PidList = pids,
            Down = (a.TickDown + prev.down) / span,
            Up = (a.TickUp + prev.up) / span,
            TotalDown = total.down,
            TotalUp = total.up,
            Established = a.Established,
            ListeningPorts = a.ListenPorts.Count,
            Udp = a.Udp,
            TcpAll = a.TcpAll,
            Conns = a.Conns.ToArray(),
        };
    }

    /// <summary>Health of the per-app measurement, with automatic restarts (up to 3) if the session dies.</summary>
    private string? EtwStatus(EtwNetworkSession? etw, CancellationToken ct)
    {
        if (!_admin) return null;
        if (_etwBlockedByOtherInstance) return "Per-app speeds are being measured by another copy of WinScope, so they're off in this one.";
        if (etw == null) return "Per-app speeds couldn't be started.";
        switch (etw.State)
        {
            case EtwState.Starting:
                // Normally running within a second; only say something if it's slow.
                return Environment.TickCount64 - etw.CreatedTick > 10_000 ? "Per-app measurement is taking a while to start…" : null;
            case EtwState.Running:
                // Sanity check: real traffic but not a single event after 15 s means the provider isn't delivering.
                if (etw.EventsReceived == 0 && (DateTime.UtcNow - etw.StartedUtc).TotalSeconds > 15 &&
                    Interlocked.Read(ref _rxSinceEtwStart) > 2 * 1024 * 1024)
                    return "Windows hasn't sent any per-app data yet, so app speeds may read zero.";
                return null;
            case EtwState.Failed:
                long nowMs = Environment.TickCount64;
                if (_etwFailedAt == 0) _etwFailedAt = nowMs;
                if (_etwRestarts < 3 && nowMs - _etwFailedAt > 20_000 && !ct.IsCancellationRequested)
                {
                    _etwRestarts++;
                    _etwFailedAt = 0;
                    try { etw.Dispose(); } catch (Exception ex) { NetLog.Error(ex); }
                    StartEtw(first: false);
                    return "Restarting per-app measurement…";
                }
                return $"Per-app speeds aren't available: {etw.Reason}. Connections are still shown.";
            default:
                return null;
        }
    }

    /// <summary>Stop the loop and the ETW session now, without waiting (shell calls this on every tool first).</summary>
    public void BeginStop()
    {
        _disposed = true;
        try { Volatile.Read(ref _cts)?.Cancel(); } catch (ObjectDisposedException) { }
        try { Volatile.Read(ref _etw)?.BeginStop(); } catch (Exception ex) { NetLog.Error(ex); }
    }

    /// <summary>BeginStop, then wait at most ~0.9 s in total (loop 300 ms + ETW thread 600 ms).</summary>
    public void Dispose()
    {
        BeginStop();
        var cts = Interlocked.Exchange(ref _cts, null);
        if (cts != null)
        {
            try { _loop?.Wait(300); } catch (AggregateException) { }
            cts.Dispose();
        }
        // Interlocked.Exchange is a full fence, so a restart racing with this either sees _disposed or hands us its session.
        try { Interlocked.Exchange(ref _etw, null)?.Dispose(600); } catch (Exception ex) { NetLog.Error(ex); }
        if (_etwMutex != null)
        {
            try { _etwMutex.ReleaseMutex(); } catch { /* not owned by this thread */ }
            _etwMutex.Dispose();
            _etwMutex = null;
        }
    }

    private sealed class Acc
    {
        public Acc(string name, string? path) { Name = name; Path = path; }
        public string Name { get; }
        public string? Path { get; set; }
        public HashSet<uint> Pids { get; } = new();
        public List<ConnInfo> Conns { get; } = new();
        public HashSet<int> ListenPorts { get; } = new();
        public int Established, Udp, TcpAll;
        public bool Placeholder;
        public long TickDown, TickUp;
    }
}
