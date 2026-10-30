using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using static WinScope.Modules.Network.NetNative;

namespace WinScope.Modules.Network;

public enum EtwState { Starting, Running, Failed, Stopped }

/// <summary>
/// Per-process network bytes from the Microsoft-Windows-Kernel-Network ETW provider, in a private real-time
/// session named "WinScope-Network" (admin only). The event callback only adds bytes to per-PID counters;
/// the monitor's 1 s loop drains them into rates.
///
/// Lifetime: Start() spins up one dedicated background thread that starts the session, enables the provider,
/// opens the real-time consumer and blocks in ProcessTrace. Dispose() stops the session and closes the consumer
/// handle (which makes ProcessTrace return), then joins the thread for a bounded time. Native memory owned by
/// the thread (the EVENT_TRACE_LOGFILEW and the logger-name string) is freed by the thread itself after
/// ProcessTrace has returned, so a slow exit can never free memory that ETW is still using.
/// </summary>
internal sealed class EtwNetworkSession : IDisposable
{
    public const string SessionName = "WinScope-Network";

    /// <summary>Microsoft-Windows-Kernel-Network.</summary>
    public static readonly Guid ProviderGuid = new("7DD42A49-5329-4832-8DFD-43D979153A88");

    // Manifest keywords (logman query providers Microsoft-Windows-Kernel-Network):
    //   0x10 KERNEL_NETWORK_KEYWORD_IPV4, 0x20 KERNEL_NETWORK_KEYWORD_IPV6.
    private const ulong KeywordIpv4 = 0x10, KeywordIpv6 = 0x20;
    private const byte LevelVerbose = 5;

    // Event IDs (task KERNEL_NETWORK_TASK_TCPIP / _UDPIP; the classic TcpIp/UdpIp MOF opcodes, IPv6 = IPv4 + 16):
    //   TCP IPv4: 10 data sent, 11 data received    TCP IPv6: 26 sent, 27 received
    //   UDP IPv4: 42 data sent, 43 data received    UDP IPv6: 58 sent, 59 received
    // Payload for all eight starts: UInt32 PID, UInt32 size, then daddr, saddr (IPv4: UInt32 each at 8 and 12;
    // IPv6: 16 bytes each at 8 and 24), dport, sport, … The header's ProcessId is the thread context the event was
    // logged in (often System for receives), NOT the owning app, so the payload PID is used.

    // ---- x64 EVENT_RECORD offsets (evntcons.h); EVENT_HEADER is 80 bytes:
    //   EventHeader.ProcessId 12, EventHeader.ProviderId 24 (GUID), EventHeader.EventDescriptor.Id 40 (USHORT),
    //   BufferContext 80, ExtendedDataCount 84, UserDataLength 86 (USHORT), ExtendedData 88, UserData 96, UserContext 104.
    internal const int RecProviderId = 24, RecEventId = 40, RecUserDataLength = 86, RecUserData = 96;

    // ---- x64 EVENT_TRACE_PROPERTIES (evntrace.h), 120 bytes, WNODE_HEADER first (48 bytes):
    //   Wnode.BufferSize 0, Wnode.Guid 24, Wnode.ClientContext 40, Wnode.Flags 44,
    //   BufferSize 48 (KB), MinimumBuffers 52, MaximumBuffers 56, MaximumFileSize 60, LogFileMode 64, FlushTimer 68,
    //   EnableFlags 72, AgeLimit 76, NumberOfBuffers 80 … LoggerThreadId 104 (HANDLE), LogFileNameOffset 112, LoggerNameOffset 116.
    private const int PropsSize = 120;
    private const int PropWnodeBufferSize = 0, PropClientContext = 40, PropWnodeFlags = 44, PropBufferSize = 48,
        PropMinBuffers = 52, PropMaxBuffers = 56, PropLogFileMode = 64, PropFlushTimer = 68,
        PropLogFileNameOffset = 112, PropLoggerNameOffset = 116;
    private const int NameChars = 1024;
    internal static int MaxBuffers => Math.Max(64, 4 * Environment.ProcessorCount);   // room for the names ControlTrace may copy back

    // ---- x64 EVENT_TRACE_LOGFILEW (evntrace.h), 448 bytes:
    //   LogFileName 0, LoggerName 8, CurrentTime 16, BuffersRead 24, LogFileMode/ProcessTraceMode 28,
    //   CurrentEvent 32 (EVENT_TRACE, 88), LogfileHeader 120 (TRACE_LOGFILE_HEADER, 280), BufferCallback 400,
    //   BufferSize 408, Filled 412, EventsLost 416, (pad) EventRecordCallback 424, IsKernelTrace 432, (pad) Context 440.
    private const int LogfileSize = 448;
    private const int LfLoggerName = 8, LfProcessTraceMode = 28, LfEventRecordCallback = 424;

    private static readonly long ProvLo, ProvHi;
    static EtwNetworkSession()
    {
        var b = ProviderGuid.ToByteArray();   // same byte order as the GUID in memory
        ProvLo = BitConverter.ToInt64(b, 0);
        ProvHi = BitConverter.ToInt64(b, 8);
    }

    public sealed class PidCounter
    {
        public readonly uint Pid;
        public long Down, Up;
        public volatile bool Removed;
        public int IdleTicks;   // monitor thread only
        public PidCounter(uint pid) => Pid = pid;
    }

    private readonly ConcurrentDictionary<uint, PidCounter> _counters = new();
    private PidCounter? _last;                                // ETW thread only
    private readonly EventRecordCallback _callback;           // kept alive for as long as ETW may call it
    private readonly object _gate = new();
    private ulong _session;                                   // under _gate
    private bool _sessionOpen;
    private ulong _consumer;
    private bool _consumerOpen;
    private bool _stopping;
    private Thread? _thread;
    private long _events;

    private volatile EtwState _state = EtwState.Starting;
    private volatile string? _reason;

    public EtwState State => _state;
    /// <summary>Why it failed (plain English), when <see cref="State"/> is Failed.</summary>
    public string? Reason => _reason;
    public long EventsReceived => Interlocked.Read(ref _events);
    public DateTime StartedUtc { get; private set; }
    public long CreatedTick { get; } = Environment.TickCount64;

    /// <summary>Set when the session is (or may be) running, so a crash handler can stop it by name.</summary>
    public static volatile bool OwnsSessionName;

    public EtwNetworkSession() => _callback = OnEvent;

    public void Start()
    {
        if (IntPtr.Size != 8) { Fail("per-app speeds need the 64-bit version of WinScope"); return; }
        _thread = new Thread(Run) { IsBackground = true, Name = "WinScope ETW network", Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    private void Fail(string why) { _reason = why; _state = EtwState.Failed; }

    private void Run()
    {
        IntPtr logfile = IntPtr.Zero, loggerName = IntPtr.Zero;
        try
        {
            if (!StartSession(out ulong session, out string? why)) { Fail(why!); return; }
            lock (_gate)
            {
                _session = session; _sessionOpen = true;
                if (_stopping) return;   // finally stops it
            }

            var guid = ProviderGuid;
            uint rc = EnableTraceEx2(session, ref guid, EVENT_CONTROL_CODE_ENABLE_PROVIDER, LevelVerbose,
                KeywordIpv4 | KeywordIpv6, 0, 0, IntPtr.Zero);
            if (rc != NO_ERROR) { Fail($"Windows refused to enable the network provider (error {rc})"); return; }

            logfile = Marshal.AllocHGlobal(LogfileSize);
            Zero(logfile, LogfileSize);
            loggerName = Marshal.StringToHGlobalUni(SessionName);
            Marshal.WriteIntPtr(logfile, LfLoggerName, loggerName);
            Marshal.WriteInt32(logfile, LfProcessTraceMode, unchecked((int)(PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD)));
            Marshal.WriteIntPtr(logfile, LfEventRecordCallback, Marshal.GetFunctionPointerForDelegate(_callback));

            ulong consumer = OpenTrace(logfile);
            if (consumer == INVALID_PROCESSTRACE_HANDLE)
            {
                int err = Marshal.GetLastWin32Error();
                Fail($"couldn't open the trace for reading (error {err})");
                return;
            }
            lock (_gate)
            {
                _consumer = consumer; _consumerOpen = true;
                if (_stopping) return;
            }

            StartedUtc = DateTime.UtcNow;
            _state = EtwState.Running;
            rc = ProcessTrace(new[] { consumer }, 1, IntPtr.Zero, IntPtr.Zero);
            if (Volatile.Read(ref _stopping)) _state = EtwState.Stopped;
            else Fail(rc == NO_ERROR || rc == ERROR_CANCELLED
                ? "the trace session was stopped by Windows or another program"
                : $"reading the trace stopped (error {rc})");
        }
        catch (Exception ex)
        {
            NetLog.Error(ex);
            Fail("unexpected error: " + ex.Message);
        }
        finally
        {
            lock (_gate)
            {
                if (_consumerOpen) { CloseTrace(_consumer); _consumerOpen = false; }
                if (_sessionOpen) { StopByHandle(_session); _sessionOpen = false; OwnsSessionName = false; }
            }
            // ProcessTrace has returned (or was never called) and the consumer is closed: ETW no longer uses these.
            if (logfile != IntPtr.Zero) Marshal.FreeHGlobal(logfile);
            if (loggerName != IntPtr.Zero) Marshal.FreeHGlobal(loggerName);
        }
    }

    /// <summary>StartTrace; if a session with our name is left over (e.g. a previous WinScope crashed), stop it and retry.</summary>
    private bool StartSession(out ulong session, out string? why)
    {
        session = 0; why = null;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            IntPtr props = NewProperties(forStart: true, out _);
            uint rc;
            try { rc = StartTrace(out session, SessionName, props); }
            finally { Marshal.FreeHGlobal(props); }
            if (rc == NO_ERROR) { OwnsSessionName = true; return true; }
            if (rc == ERROR_ALREADY_EXISTS && attempt == 0)
            {
                StopByName();   // stale session from a crashed run (another live WinScope is ruled out by the mutex)
                continue;
            }
            why = rc switch
            {
                ERROR_ACCESS_DENIED => "Windows denied access to start a trace session (error 5)",
                ERROR_ALREADY_EXISTS => "a trace session called WinScope-Network is already running and couldn't be stopped",
                ERROR_NO_SYSTEM_RESOURCES => "too many trace sessions are running (Windows allows 64)",
                ERROR_BAD_LENGTH or ERROR_INVALID_PARAMETER => $"Windows rejected the trace settings (error {rc})",
                _ => $"couldn't start a trace session (error {rc})"
            };
            return false;
        }
        why = "couldn't start a trace session";
        return false;
    }

    /// <summary>EVENT_TRACE_PROPERTIES followed by room for the logger name (and, for ControlTrace, a log file name).</summary>
    private static IntPtr NewProperties(bool forStart, out int size)
    {
        size = PropsSize + NameChars * 2 * 2;
        IntPtr p = Marshal.AllocHGlobal(size);
        Zero(p, size);
        Marshal.WriteInt32(p, PropWnodeBufferSize, size);
        Marshal.WriteInt32(p, PropWnodeFlags, unchecked((int)WNODE_FLAG_TRACED_GUID));
        Marshal.WriteInt32(p, PropLoggerNameOffset, PropsSize);
        if (forStart)
        {
            Marshal.WriteInt32(p, PropClientContext, 1);          // 1 = QueryPerformanceCounter timestamps
            Marshal.WriteInt32(p, PropBufferSize, 64);            // KB per buffer
            // Minimum 0 = let ETW pick (its default and floor is 2 per CPU); the maximum bounds kernel memory
            // (64 KB × this) if the consumer falls behind or dies.
            Marshal.WriteInt32(p, PropMinBuffers, 0);
            Marshal.WriteInt32(p, PropMaxBuffers, MaxBuffers);
            Marshal.WriteInt32(p, PropLogFileMode, unchecked((int)EVENT_TRACE_REAL_TIME_MODE));
            Marshal.WriteInt32(p, PropFlushTimer, 1);             // deliver at least every second, even when quiet
            Marshal.WriteInt32(p, PropLogFileNameOffset, 0);      // real-time only, no file
            // StartTrace copies the session name into the buffer at LoggerNameOffset itself.
        }
        else
        {
            Marshal.WriteInt32(p, PropLogFileNameOffset, PropsSize + NameChars * 2);
        }
        return p;
    }

    private static void StopByHandle(ulong session)
    {
        IntPtr props = NewProperties(forStart: false, out _);
        try { ControlTrace(session, null, props, EVENT_TRACE_CONTROL_STOP); }
        catch { }
        finally { Marshal.FreeHGlobal(props); }
    }

    /// <summary>Stop any session called WinScope-Network. Safe to call when none exists (returns 4201).</summary>
    public static uint StopByName()
    {
        IntPtr props = NewProperties(forStart: false, out _);
        try { return ControlTrace(0, SessionName, props, EVENT_TRACE_CONTROL_STOP); }
        catch { return uint.MaxValue; }
        finally { Marshal.FreeHGlobal(props); }
    }

    private static void Zero(IntPtr p, int size)
    {
        int i = 0;
        for (; i + 8 <= size; i += 8) Marshal.WriteInt64(p, i, 0);
        for (; i < size; i++) Marshal.WriteByte(p, i, 0);
    }

    // ================================================================== event callback (ETW thread, hot path)

    private void OnEvent(IntPtr rec)
    {
        try
        {
            if (!TryParse(rec, out uint pid, out uint size, out bool down)) return;
            Interlocked.Increment(ref _events);
            var c = CounterFor(pid);
            if (down) Interlocked.Add(ref c.Down, size);
            else Interlocked.Add(ref c.Up, size);
        }
        catch { /* never let an exception cross back into ETW */ }
    }

    private PidCounter CounterFor(uint pid)
    {
        var c = _last;
        if (c != null && c.Pid == pid && !c.Removed) return c;
        c = _counters.GetOrAdd(pid, static p => new PidCounter(p));
        if (c.Removed) c = _counters.GetOrAdd(pid, static p => new PidCounter(p));   // raced with a prune
        _last = c;
        return c;
    }

    /// <summary>
    /// Parse one EVENT_RECORD: true for a Kernel-Network send/receive that isn't loopback. Pure (reads only the
    /// record), so it's unit-tested with synthetic buffers.
    /// </summary>
    internal static bool TryParse(IntPtr rec, out uint pid, out uint size, out bool down)
    {
        pid = 0; size = 0; down = false;
        if (rec == IntPtr.Zero) return false;
        if (Marshal.ReadInt64(rec, RecProviderId) != ProvLo || Marshal.ReadInt64(rec, RecProviderId + 8) != ProvHi) return false;
        int id = (ushort)Marshal.ReadInt16(rec, RecEventId);
        bool v6;
        switch (id)
        {
            case 10: case 42: v6 = false; down = false; break;   // TCP/UDP IPv4 send
            case 11: case 43: v6 = false; down = true; break;    // TCP/UDP IPv4 receive
            case 26: case 58: v6 = true; down = false; break;    // TCP/UDP IPv6 send
            case 27: case 59: v6 = true; down = true; break;     // TCP/UDP IPv6 receive
            default: return false;
        }
        int len = (ushort)Marshal.ReadInt16(rec, RecUserDataLength);
        IntPtr ud = Marshal.ReadIntPtr(rec, RecUserData);
        if (ud == IntPtr.Zero || len < 8) return false;
        pid = (uint)Marshal.ReadInt32(ud, 0);
        size = (uint)Marshal.ReadInt32(ud, 4);
        if (size == 0 || size > 64 * 1024 * 1024) return false;
        // Leave out loopback (app ↔ app on this PC), so per-app figures compare with the adapter totals.
        if (!v6)
        {
            if (len >= 16)
            {
                int d = Marshal.ReadInt32(ud, 8), s = Marshal.ReadInt32(ud, 12);
                if ((d & 0xFF) == 127 || (s & 0xFF) == 127) return false;   // first octet in the low byte
            }
        }
        else if (len >= 40)
        {
            if (IsV6Loopback(ud, 8) || IsV6Loopback(ud, 24)) return false;
        }
        return true;
    }

    /// <summary>::1, or IPv4-mapped loopback ::ffff:127.x.x.x (dual-stack sockets talking to 127.0.0.1).</summary>
    internal static bool IsV6Loopback(IntPtr p, int off)
    {
        if (Marshal.ReadInt64(p, off) != 0) return false;                 // bytes 0-7 must be zero for both forms
        long hi = Marshal.ReadInt64(p, off + 8);                          // bytes 8-15, little-endian
        if (hi == 0x0100000000000000L) return true;                       // ::1 (byte 15 = 1)
        // bytes 8,9 = 0; 10,11 = 0xFF; 12 = 127 → low 40 bits of hi = 0x7F_FFFF_0000
        return (hi & 0xFF_FFFF_FFFFL) == 0x7F_FFFF_0000L;
    }

    // ================================================================== draining (monitor thread)

    /// <summary>Move the bytes counted since the last call into <paramref name="into"/> (pid → down, up) and prune idle PIDs.</summary>
    public void Drain(Dictionary<uint, (long down, long up)> into)
    {
        into.Clear();
        foreach (var kv in _counters)
        {
            var c = kv.Value;
            long d = Interlocked.Exchange(ref c.Down, 0), u = Interlocked.Exchange(ref c.Up, 0);
            if (d == 0 && u == 0)
            {
                if (++c.IdleTicks > 120)
                {
                    c.Removed = true;
                    _counters.TryRemove(new KeyValuePair<uint, PidCounter>(kv.Key, c));
                    d = Interlocked.Exchange(ref c.Down, 0); u = Interlocked.Exchange(ref c.Up, 0);   // anything that slipped in
                    if (d == 0 && u == 0) continue;
                }
                else continue;
            }
            c.IdleTicks = 0;
            into[kv.Key] = (d, u);
        }
    }

    /// <summary>Stop the session and close the consumer now, without waiting for the thread (quick syscalls only).</summary>
    public void BeginStop()
    {
        lock (_gate)
        {
            _stopping = true;
            // Stopping the session ends ProcessTrace after the buffers in flight; closing the consumer makes sure.
            if (_sessionOpen) { StopByHandle(_session); _sessionOpen = false; OwnsSessionName = false; }
            if (_consumerOpen) { CloseTrace(_consumer); _consumerOpen = false; }   // ERROR_CTX_CLOSE_PENDING is expected
        }
    }

    /// <summary>BeginStop, then wait up to <paramref name="waitMs"/> for the consumer thread to exit.</summary>
    public void Dispose() => Dispose(600);

    public void Dispose(int waitMs)
    {
        BeginStop();
        var t = _thread;
        if (t != null && t != Thread.CurrentThread && !t.Join(Math.Max(0, waitMs)))
            NetLog.Error(new TimeoutException($"ETW consumer thread didn't stop within {waitMs} ms"));
        if (_state != EtwState.Failed) _state = EtwState.Stopped;
    }
}
