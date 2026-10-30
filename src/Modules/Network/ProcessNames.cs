using System.Diagnostics;
using static WinScope.Modules.Network.NetNative;

namespace WinScope.Modules.Network;

/// <summary>
/// PID → app name. A process list snapshot (names only – works without admin for every process) is re-read when
/// a PID we don't know turns up (at most once a second) and otherwise only every 15 s to notice ended/reused PIDs.
/// Process.GetProcesses() on Windows is a single NtQuerySystemInformation call and ProcessName comes from that
/// snapshot, so it's cheap (no per-process handles). Friendly names ("Google Chrome" rather than "chrome") come from the exe's version info,
/// resolved once per PID that actually shows up in the network data and cached per exe path.
/// Processes we can't open (protected/other-user processes without admin) keep their plain process name.
/// Used only from the monitor's background loop (not thread-safe).
/// </summary>
internal sealed class ProcessNames
{
    public sealed class Info
    {
        public required string Name { get; init; }      // display / grouping name
        public required string ProcName { get; init; }  // e.g. "chrome" – detects PID reuse
        public string? Path { get; init; }
        /// <summary>Process creation time (FILETIME UTC ticks), 0 if it couldn't be read.</summary>
        public long StartFileTime { get; init; }
        /// <summary>Not resolved yet (the PID isn't in the process list we have): Name is a stand-in "Process 1234".</summary>
        public bool Placeholder { get; init; }
        public long LastAliveTick { get; set; }
        public bool Alive { get; set; }
    }

    private static readonly Info SystemInfo = new() { Name = "System", ProcName = "System", Path = null, Alive = true };

    private Dictionary<uint, string> _snapshot = new();
    private readonly Dictionary<uint, Info> _byPid = new();
    private readonly Dictionary<string, string> _descByPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, long> _firstMiss = new();
    private readonly char[] _pathBuf = new char[1024];
    private long _lastSnapshot = long.MinValue / 2;
    private bool _wantRefresh;

    private const int PeriodicMs = 15_000;

    /// <summary>Call once per tick: re-reads the process list if an unknown PID turned up (≥1 s apart), else every 15 s.</summary>
    public void Tick()
    {
        long now = Environment.TickCount64;
        if (now - _lastSnapshot < PeriodicMs && !(_wantRefresh && now - _lastSnapshot >= 1000)) return;
        _wantRefresh = false;
        _lastSnapshot = now;
        var snap = new Dictionary<uint, string>(512);
        Process[] procs;
        try { procs = Process.GetProcesses(); }
        catch (Exception ex) { NetLog.Error(ex); return; }
        foreach (var p in procs)
        {
            try { snap[(uint)p.Id] = p.ProcessName; }
            catch { /* exited while listing */ }
            finally { p.Dispose(); }
        }
        _snapshot = snap;

        // Mark alive/ended, forget processes that ended over 2 minutes ago.
        List<uint>? gone = null;
        foreach (var (pid, info) in _byPid)
        {
            if (snap.TryGetValue(pid, out var pn) && string.Equals(pn, info.ProcName, StringComparison.OrdinalIgnoreCase))
            { info.Alive = true; info.LastAliveTick = now; }
            else
            {
                info.Alive = false;
                if (now - info.LastAliveTick > 120_000 || snap.ContainsKey(pid)) (gone ??= new()).Add(pid);   // ended long ago, or PID reused
            }
        }
        if (gone != null) foreach (var pid in gone) _byPid.Remove(pid);
        if (_descByPath.Count > 4000) _descByPath.Clear();
    }

    public Info Get(uint pid)
    {
        if (pid is 0 or 4 or uint.MaxValue) return SystemInfo;   // 0 = idle/TIME_WAIT owner, 4 = the kernel, -1 = unattributed
        if (_byPid.TryGetValue(pid, out var info))
        {
            if (!info.Alive || !_snapshot.TryGetValue(pid, out var pn) || string.Equals(pn, info.ProcName, StringComparison.OrdinalIgnoreCase))
                return info;
            _byPid.Remove(pid);   // PID reused by a different program
        }
        if (!_snapshot.TryGetValue(pid, out var procName))
        {
            long now = Environment.TickCount64;
            if (_firstMiss.TryGetValue(pid, out long missedAt) && _lastSnapshot > missedAt)
            {
                // Still missing after a fresh process list: it has ended. Cache that (pruned after 2 min like
                // the rest) so it doesn't keep forcing process-list refreshes.
                _firstMiss.Remove(pid);
                info = new Info { Name = $"Process {pid} (ended)", ProcName = "", Alive = false, LastAliveTick = now };
                _byPid[pid] = info;
                return info;
            }
            // Probably started after the last snapshot: ask for a refresh, don't cache a guess yet.
            if (!_firstMiss.ContainsKey(pid)) { if (_firstMiss.Count > 1000) _firstMiss.Clear(); _firstMiss[pid] = now; }
            _wantRefresh = true;
            return new Info { Name = $"Process {pid}", ProcName = "", Alive = false, Placeholder = true };
        }
        _firstMiss.Remove(pid);
        string? path = ImagePath(pid, out long started);
        info = new Info
        {
            Name = FriendlyName(procName, path),
            ProcName = procName,
            Path = path,
            StartFileTime = started,
            Alive = true,
            LastAliveTick = Environment.TickCount64
        };
        _byPid[pid] = info;
        return info;
    }

    private string? ImagePath(uint pid, out long startFileTime)
    {
        startFileTime = 0;
        IntPtr h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (h == IntPtr.Zero) return null;   // access denied (protected / other user) or exited
        try
        {
            if (GetProcessTimes(h, out long created, out _, out _, out _) && created > 0) startFileTime = created;
            int size = _pathBuf.Length;
            if (!QueryFullProcessImageName(h, 0, _pathBuf, ref size) || size <= 0 || size > _pathBuf.Length) return null;
            return new string(_pathBuf, 0, size);
        }
        finally { CloseHandle(h); }
    }

    private string FriendlyName(string procName, string? path)
    {
        if (procName.Equals("svchost", StringComparison.OrdinalIgnoreCase)) return "Windows services";
        if (procName.Equals("System", StringComparison.OrdinalIgnoreCase) || procName.Equals("Idle", StringComparison.OrdinalIgnoreCase)) return "System";
        if (path == null) return procName;
        if (!_descByPath.TryGetValue(path, out var desc))
        {
            desc = "";
            try
            {
                var d = FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                if (!string.IsNullOrEmpty(d) && d.Length <= 48 && !d.Contains('\n')) desc = d;
            }
            catch { /* no version resource / file not readable */ }
            _descByPath[path] = desc;
        }
        return desc.Length > 0 ? desc : procName;
    }
}
