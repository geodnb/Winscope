using System.Collections.Concurrent;

namespace WinScope.Modules.Events;

/// <summary>What the views get after each refresh. Immutable.</summary>
public sealed class EventsSnapshot
{
    public IReadOnlyList<EventGroup> Groups { get; init; } = Array.Empty<EventGroup>();
    public bool Loaded { get; init; }
    /// <summary>Plain-English notes: unreadable logs, read caps hit.</summary>
    public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    /// <summary>Events older than this may be missing because a log hit the read cap (null = complete).</summary>
    public DateTime? CompleteSinceUtc { get; init; }
    public DateTime TakenUtc { get; init; }
    /// <summary>Most recent Windows start seen in the log (Kernel-Boot 27 / Kernel-General 12), or null.</summary>
    public DateTime? LastStartUtc { get; init; }
    /// <summary>Boot type of <see cref="LastStartUtc"/>: 0 full start, 1 Fast Startup, 2 resume from hibernate, -1 unknown.</summary>
    public int LastStartKind { get; init; } = -1;
}

/// <summary>
/// Background collection: one loop reads the logs (full read at start, then every 60 s only records newer than the
/// last pass's high-water mark), classifies and aggregates them, and raises <see cref="Updated"/>. A second loop
/// formats full messages on demand (newest request first, bounded queue). Neither loop touches the UI.
/// </summary>
internal sealed class EventsEngine : IDisposable
{
    public static readonly string[] Logs = { "System", "Application" };
    public static readonly TimeSpan Window = TimeSpan.FromDays(30);
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ClockJump = TimeSpan.FromMinutes(10);
    private const int MaxQueuedMessages = 64, MaxCachedMessages = 4000;
    private const long MaxCachedChars = 4_000_000;   // ~8 MB of text, whichever limit comes first

    private sealed class LogState
    {
        public long MaxId;
        public long Oldest;
        public int Generation;
        public DateTime LastReadUtc;
        public string? Error;
        public DateTime? TruncatedBeforeUtc;
    }

    private readonly Dictionary<string, Classified> _occ = new(StringComparer.Ordinal);   // loop thread only
    private readonly Dictionary<string, LogState> _logs = Logs.ToDictionary(l => l, _ => new LogState());
    private List<EventGroup> _groups = new();
    private bool _groupsDirty = true;
    private int _regroup;                            // 1 = time zone changed: rebuild groups (local dates) next pass
    private DateTime _lastWallUtc;
    private long _lastTick;
    private bool _systemDiskChecked;

    private CancellationTokenSource? _cts;
    private Task? _loop, _msgLoop;
    private readonly SemaphoreSlim _kick = new(0, 1);
    private readonly SemaphoreSlim _msgSignal = new(0, 1);

    private readonly object _msgLock = new();
    private readonly LinkedList<RawEvent> _msgQueue = new();
    private readonly HashSet<string> _msgQueued = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _msgCache = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _ready = new();
    private long _cachedChars;                       // message loop only (and ClearMessages under _msgLock)

    /// <summary>Raised on a background thread after every refresh (about once a minute).</summary>
    public event Action<EventsSnapshot>? Updated;
    /// <summary>Raised on a background thread when one or more requested messages are ready (see <see cref="DequeueReady"/>).</summary>
    public event Action? MessagesReady;

    public void Start()
    {
        if (_cts != null) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _loop = Task.Run(() => Loop(ct));
        _msgLoop = Task.Run(() => MessageLoop(ct));
    }

    /// <summary>Refresh now instead of waiting for the next minute.</summary>
    public void Kick() => Signal(_kick);

    /// <summary>The time zone changed: local dates need regrouping. Safe from any thread.</summary>
    public void TimeZoneChanged()
    {
        Interlocked.Exchange(ref _regroup, 1);
        Signal(_kick);
    }

    private static void Signal(SemaphoreSlim s)
    {
        try { if (s.CurrentCount == 0) s.Release(); }
        catch (SemaphoreFullException) { }
        catch (ObjectDisposedException) { }
    }

    // ------------------------------------------------------------------ collection loop

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { RefreshOnce(ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { App.LogError(ex); }
            try { await _kick.WaitAsync(Interval, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
        }
    }

    private void ForgetLog(string log, LogState st, bool cleared)
    {
        foreach (var k in _occ.Where(kv => kv.Value.Ev.Log == log).Select(kv => kv.Key).ToList()) _occ.Remove(k);
        st.MaxId = 0;
        st.TruncatedBeforeUtc = null;
        if (cleared)
        {
            st.Generation++;          // new keys: record ids restart after a clear
            st.Oldest = 0;
            ClearMessages(log);
        }
        _groupsDirty = true;
    }

    private void RefreshOnce(CancellationToken ct)
    {
        if (!_systemDiskChecked)
        {
            _systemDiskChecked = true;
            try { EventRules.SystemDiskNumber = SystemDisk.Number(); }
            catch (Exception ex) { App.LogError(ex); }
        }

        var now = DateTime.UtcNow;
        long tick = Environment.TickCount64;
        // Wall clock stepped (user changed it, or a big time sync) relative to the monotonic tick count: the 30-day
        // window and pruning are wall-clock based, so start again from a full read.
        if (_lastTick != 0)
        {
            var wall = now - _lastWallUtc;
            var mono = TimeSpan.FromMilliseconds(tick - _lastTick);
            if ((wall - mono).Duration() > ClockJump)
                foreach (var log in Logs) ForgetLog(log, _logs[log], cleared: false);
        }
        _lastWallUtc = now;
        _lastTick = tick;

        if (Interlocked.Exchange(ref _regroup, 0) == 1)
        {
            TimeZoneInfo.ClearCachedData();
            _groupsDirty = true;
        }

        foreach (var log in Logs)
        {
            ct.ThrowIfCancellationRequested();
            var st = _logs[log];
            try
            {
                // High-water mark first: every query in this pass is limited to ids ≤ this, so the pass is exact.
                long hw = 0;
                try { hw = EventReader.NewestRecordId(log); }
                catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException) { hw = 0; }

                // Cleared? Record ids restart from 1 and the oldest record number drops.
                long? oldest = null;
                try { oldest = EventReader.OldestRecordNumber(log); }
                catch (Exception ex) when (ex is System.Diagnostics.Eventing.Reader.EventLogException or UnauthorizedAccessException) { }
                bool cleared = st.MaxId > 0 && ((hw > 0 && hw < st.MaxId) || (oldest is long o && st.Oldest > 0 && o < st.Oldest));
                if (cleared) ForgetLog(log, st, cleared: true);
                if (oldest is long o2 && o2 > 0) st.Oldest = o2;

                var res = ReadPass(log, st, hw, now, ct);
                // An incremental pass that hit the read cap has dropped some events in the middle: re-read from scratch
                // (the full read reports any cap honestly as "only the newest N were read").
                if (st.MaxId > 0 && res.wasIncremental && res.result.TruncatedBeforeUtc != null && !ct.IsCancellationRequested)
                {
                    ForgetLog(log, st, cleared: false);
                    res = ReadPass(log, st, hw, now, ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                st.Error = ex.Message;
                App.LogError(ex);
            }
        }
        ct.ThrowIfCancellationRequested();

        // Drop what has slid out of the 30-day window.
        var cutoff = now - Window - TimeSpan.FromHours(1);
        var old = _occ.Where(kv => kv.Value.TimeUtc < cutoff).Select(kv => kv.Key).ToList();
        foreach (var k in old) _occ.Remove(k);
        if (old.Count > 0) _groupsDirty = true;

        if (_groupsDirty)
        {
            _groups = EventAggregator.Build(_occ.Values);
            _groupsDirty = false;
        }

        var notes = new List<string>();
        DateTime? complete = null;
        foreach (var log in Logs)
        {
            var st = _logs[log];
            if (st.Error != null) notes.Add($"Couldn't read the {log} log: {st.Error}.");
            if (st.TruncatedBeforeUtc is DateTime tb)
            {
                notes.Add($"The {log} log has a lot of entries, so only the newest {EventReader.MainCap:N0} were read (back to {tb.ToLocalTime():d MMM HH:mm}).");
                if (complete == null || tb > complete) complete = tb;
            }
        }

        // Latest start: prefer Kernel-Boot 27 (knows the boot type), else Kernel-General 12.
        var starts = _occ.Values.Where(c => c.Rule is "kb27" or "kg12").ToList();
        Classified? start = starts.MaxBy(c => c.TimeUtc);
        if (start != null && start.Rule == "kg12")
        {
            var t0 = start.TimeUtc;
            start = starts.Where(c => c.Rule == "kb27" && (c.TimeUtc - t0).Duration() < TimeSpan.FromMinutes(3)).MaxBy(c => c.TimeUtc) ?? start;
        }

        var snap = new EventsSnapshot
        {
            Groups = _groups,
            Loaded = true,
            Notes = notes,
            CompleteSinceUtc = complete,
            TakenUtc = now,
            LastStartUtc = start?.TimeUtc,
            LastStartKind = start == null ? -1 : start.Rule == "kb27" ? (int)Math.Min(start.Code, 9) : -1,
        };
        try { Updated?.Invoke(snap); }
        catch (Exception ex) { App.LogError(ex); }
    }

    private (EventReader.Result result, bool wasIncremental) ReadPass(string log, LogState st, long hw, DateTime now, CancellationToken ct)
    {
        bool full = st.MaxId == 0;
        // Incremental reads still carry a (generous) time window in case the record-id filters have to be dropped.
        long windowMs = full ? (long)Window.TotalMilliseconds
            : (long)Math.Min(Window.TotalMilliseconds, (now - st.LastReadUtc + TimeSpan.FromMinutes(10)).Duration().TotalMilliseconds);
        var res = EventReader.Read(log, st.Generation, st.MaxId, hw, windowMs, ct);
        st.LastReadUtc = now;
        st.Error = res.Error;
        if (full) st.TruncatedBeforeUtc = res.TruncatedBeforeUtc;
        if (res.MaxRecordId > st.MaxId) st.MaxId = res.MaxRecordId;
        foreach (var raw in res.Events)
        {
            if (_occ.ContainsKey(raw.Key)) continue;
            Classified? c;
            try { c = EventRules.Classify(raw); }
            catch (Exception ex) { App.LogError(ex); c = null; }
            if (c == null) continue;
            _occ[raw.Key] = c;
            _groupsDirty = true;
        }
        return (res, !full);
    }

    // ------------------------------------------------------------------ lazy messages

    /// <summary>The formatted message if it has been loaded, else null.</summary>
    public string? GetMessage(string key) => _msgCache.TryGetValue(key, out var m) ? m : null;

    /// <summary>Ask for an event's formatted message. Cheap and idempotent; safe from any thread.</summary>
    public void RequestMessage(RawEvent ev)
    {
        if (_msgCache.ContainsKey(ev.Key)) return;
        lock (_msgLock)
        {
            if (!_msgQueued.Add(ev.Key)) return;
            _msgQueue.AddFirst(ev);                      // newest request first: what's on screen now
            while (_msgQueue.Count > MaxQueuedMessages)
            {
                var last = _msgQueue.Last!.Value;        // scrolled past long ago; asked again if it comes back into view
                _msgQueue.RemoveLast();
                _msgQueued.Remove(last.Key);
            }
        }
        Signal(_msgSignal);
    }

    /// <summary>A log was cleared: forget its cached messages and pending requests.</summary>
    private void ClearMessages(string log)
    {
        string prefix = log + "#";
        lock (_msgLock)
        {
            var node = _msgQueue.First;
            while (node != null)
            {
                var next = node.Next;
                if (node.Value.Log == log) { _msgQueued.Remove(node.Value.Key); _msgQueue.Remove(node); }
                node = next;
            }
            foreach (var k in _msgCache.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList())
                if (_msgCache.TryRemove(k, out var m)) Interlocked.Add(ref _cachedChars, -m.Length);
        }
    }

    /// <summary>Next event key whose message has just been loaded, or null.</summary>
    public string? DequeueReady() => _ready.TryDequeue(out var k) ? k : null;

    private async Task MessageLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try { await _msgSignal.WaitAsync(ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }
            catch (ObjectDisposedException) { return; }
            while (!ct.IsCancellationRequested)
            {
                RawEvent ev;
                lock (_msgLock)
                {
                    if (_msgQueue.First == null) break;
                    ev = _msgQueue.First.Value;
                    _msgQueue.RemoveFirst();
                }
                string msg;
                try { msg = EventReader.FormatMessage(ev); }
                catch (Exception ex) { msg = "Couldn't read the full message: " + ex.Message; }
                lock (_msgLock)
                {
                    // Dropped while we were formatting (log cleared): don't cache a message under a stale key.
                    if (!_msgQueued.Remove(ev.Key)) continue;
                    if (_msgCache.Count >= MaxCachedMessages || Interlocked.Read(ref _cachedChars) + msg.Length > MaxCachedChars)
                    {
                        _msgCache.Clear();               // on-screen items simply ask again
                        Interlocked.Exchange(ref _cachedChars, 0);
                    }
                    _msgCache[ev.Key] = msg;
                    Interlocked.Add(ref _cachedChars, msg.Length);
                }
                _ready.Enqueue(ev.Key);
                try { MessagesReady?.Invoke(); }
                catch (Exception ex) { App.LogError(ex); }
            }
        }
    }

    // ------------------------------------------------------------------ shutdown

    /// <summary>Stop now without waiting: reads check the token between records and exit promptly.</summary>
    public void BeginStop()
    {
        try { _cts?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    public void Dispose()
    {
        var cts = _cts;
        if (cts == null) return;
        _cts = null;
        try { cts.Cancel(); } catch (ObjectDisposedException) { }
        var tasks = new[] { _loop, _msgLoop }.Where(t => t != null).Cast<Task>().ToArray();
        bool done = false;
        try { done = Task.WaitAll(tasks, 1000); }
        catch (AggregateException) { done = true; }
        if (done) cts.Dispose();   // else leave it to the GC: a loop still finishing may touch its token
        Updated = null;
        MessagesReady = null;
    }
}
