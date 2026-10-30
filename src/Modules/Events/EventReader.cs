using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Text;

namespace WinScope.Modules.Events;

/// <summary>
/// Reads the System and Application logs with System.Diagnostics.Eventing.Reader (no admin needed for these two).
/// Records are copied into <see cref="RawEvent"/> and disposed straight away – we never keep EventRecord objects
/// (each holds a native handle). FormatDescription() is slow, so it's only called by <see cref="FormatMessage"/>
/// when the user actually looks at an event. All methods run on background threads.
/// </summary>
internal static class EventReader
{
    public const int MainCap = 5000, NoiseCap = 1500, PlainExtraCap = 1000;
    private const int MaxProps = 64, MaxString = 4096, MaxMessage = 20000;

    public sealed class Result
    {
        public readonly List<RawEvent> Events = new();
        /// <summary>Everything up to here has been covered (the pass's high-water mark, or the newest id seen).</summary>
        public long MaxRecordId;
        /// <summary>Set when the main read hit its cap: events older than this may be missing.</summary>
        public DateTime? TruncatedBeforeUtc;
        public string? Error;
        /// <summary>Message of the last generic EventLogException (used if every query form failed).</summary>
        public string? LastFailure;
        public bool AnySucceeded;
        /// <summary>A query broke off (repeated read errors or cancellation): don't advance past this pass.</summary>
        public bool Incomplete;
    }

    /// <summary>
    /// Read one log: records in (afterId, highWater] from the last windowMs. highWater is the log's newest record id
    /// taken before the pass (0 = unknown, no upper bound), applied to the main and the noise query alike so the
    /// pass covers exactly that range and <see cref="Result.MaxRecordId"/> can safely be set to it.
    /// </summary>
    public static Result Read(string log, int generation, long afterId, long highWater, long windowMs, CancellationToken ct)
    {
        var res = new Result();
        if (highWater > 0 && highWater <= afterId) { res.MaxRecordId = afterId; res.AnySucceeded = true; return res; }  // nothing new
        var b = new ReadBounds(afterId, highWater, windowMs);
        var seen = new HashSet<long>();
        var selectors = new Dictionary<Rule, EventLogPropertySelector?>();
        try
        {
            bool complete = false;
            bool ok = TryRun(() => ReadQuery(log, generation, EventQueries.MainStructured(log, b), MainCap, res, seen, selectors, ct, true), res);
            if (ok)
                complete = TryRun(() => ReadQuery(log, generation, EventQueries.NoiseStructured(log, b), NoiseCap, res, seen, selectors, ct, false), res);
            else if (res.Error == null)
            {
                // Structured query rejected (EventLogException other than not-found/denied): fall back to plain
                // XPath queries one at a time. Harmless noise then shares the main cap – acceptable.
                complete = RunPlain(log, generation, b, res, seen, selectors, ct);
                if (!complete && res.Error == null && (afterId > 0 || highWater > 0))
                    // Last resort if the EventRecordID comparisons aren't accepted: time window only (engine de-duplicates).
                    complete = RunPlain(log, generation, new ReadBounds(0, 0, windowMs), res, seen, selectors, ct);
            }
            if (!complete) res.Incomplete = true;
        }
        finally
        {
            foreach (var s in selectors.Values) s?.Dispose();
        }
        if (!res.AnySucceeded && res.Error == null && res.LastFailure != null) res.Error = res.LastFailure.TrimEnd('.');
        if (ct.IsCancellationRequested) res.Incomplete = true;
        // Only advance past the high-water mark when every query of the pass finished; otherwise stay put and let
        // the next pass re-read the range (duplicates are dropped by key).
        if (res.Incomplete) res.MaxRecordId = afterId;
        else if (highWater > res.MaxRecordId) res.MaxRecordId = highWater;
        return res;
    }

    private static bool RunPlain(string log, int generation, ReadBounds b, Result res, HashSet<long> seen,
        Dictionary<Rule, EventLogPropertySelector?> selectors, CancellationToken ct)
    {
        var plain = EventQueries.MainPlain(log, b);
        bool all = true;
        for (int i = 0; i < plain.Count && !ct.IsCancellationRequested; i++)
        {
            string q = plain[i];
            int cap = i == 0 ? MainCap : PlainExtraCap;
            bool trunc = i == 0;
            if (!TryRun(() => ReadQuery(log, generation, q, cap, res, seen, selectors, ct, trunc), res)) all = false;
            if (res.Error != null) return false;
        }
        return all;
    }

    /// <summary>Runs a query; false on failure. Not-found / access-denied set res.Error (no point retrying other forms).</summary>
    private static bool TryRun(Action a, Result res)
    {
        try { a(); res.AnySucceeded = true; return true; }
        catch (OperationCanceledException) { throw; }
        catch (EventLogNotFoundException) { res.Error = "this log wasn't found"; }
        catch (UnauthorizedAccessException) { res.Error = "access was denied"; }
        catch (EventLogReadingException ex) { res.Error = ex.Message; }
        catch (EventLogException ex) { res.LastFailure = ex.Message; /* typically ERROR_EVT_INVALID_QUERY: caller falls back */ }
        return false;
    }

    private static void ReadQuery(string log, int generation, string query, int cap, Result res, HashSet<long> seen,
        Dictionary<Rule, EventLogPropertySelector?> selectors, CancellationToken ct, bool trackTruncation)
    {
        var q = new EventLogQuery(log, PathType.LogName, query) { ReverseDirection = true };
        using var reader = new EventLogReader(q);
        int n = 0, errors = 0;
        DateTime oldest = DateTime.MaxValue;
        while (!ct.IsCancellationRequested)
        {
            if (n >= cap)
            {
                if (trackTruncation && oldest != DateTime.MaxValue) res.TruncatedBeforeUtc = oldest;
                break;
            }
            EventRecord? r;
            try { r = reader.ReadEvent(); }
            catch (EventLogException) { if (++errors > 20) { res.Incomplete = true; break; } continue; }
            catch (UnauthorizedAccessException) { if (++errors > 20) { res.Incomplete = true; break; } continue; }
            if (r == null) break;
            n++;
            using (r)
            {
                try
                {
                    var raw = Convert(log, generation, r, selectors, out long rid, out DateTime t);
                    if (rid > res.MaxRecordId) res.MaxRecordId = rid;
                    if (t < oldest) oldest = t;
                    if (raw != null && seen.Add(raw.RecordId)) res.Events.Add(raw);
                }
                catch (EventLogException) { if (++errors > 200) { res.Incomplete = true; break; } }
                catch (UnauthorizedAccessException) { if (++errors > 200) { res.Incomplete = true; break; } }
                catch (InvalidOperationException) { if (++errors > 200) { res.Incomplete = true; break; } }
            }
        }
    }

    private static RawEvent? Convert(string log, int generation, EventRecord r, Dictionary<Rule, EventLogPropertySelector?> selectors, out long rid, out DateTime timeUtc)
    {
        rid = r.RecordId ?? 0;
        timeUtc = (r.TimeCreated ?? DateTime.Now).ToUniversalTime();
        string provider = r.ProviderName ?? "";
        int id = r.Id;
        byte level = r.Level ?? 0;
        var rule = EventRules.Find(provider, id, level);
        if (rule == null) return null;

        object?[] props = Array.Empty<object?>();
        if (rule.NeedsProps)
        {
            try
            {
                var p = r.Properties;
                int count = Math.Min(p.Count, MaxProps);
                props = new object?[count];
                for (int i = 0; i < count; i++) props[i] = Sanitize(p[i].Value);
            }
            catch (EventLogException) { props = Array.Empty<object?>(); }
        }

        Dictionary<string, object?>? named = null;
        if (rule.Names.Length > 0 && r is EventLogRecord lr)
        {
            try
            {
                if (!selectors.TryGetValue(rule, out var sel))
                {
                    try { sel = new EventLogPropertySelector(rule.Names.Select(n => "Event/EventData/Data[@Name='" + n + "']")); }
                    catch (EventLogException) { sel = null; }
                    selectors[rule] = sel;
                }
                if (sel != null)
                {
                    var vals = lr.GetPropertyValues(sel);
                    named = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    for (int i = 0; i < rule.Names.Length && i < vals.Count; i++) named[rule.Names[i]] = Sanitize(vals[i]);
                }
            }
            catch (EventLogException) { named = null; }
            catch (ArgumentException) { named = null; }
        }

        return new RawEvent
        {
            Log = log, Generation = generation, RecordId = rid, Provider = provider, Id = id, Level = level, TimeUtc = timeUtc, Props = props, Named = named
        };
    }

    /// <summary>Keep only plain values, with strings capped, so nothing large or native-backed is held.</summary>
    private static object? Sanitize(object? v) => v switch
    {
        null => null,
        string s => s.Length > MaxString ? s[..MaxString] : s,
        bool or byte or sbyte or short or ushort or int or uint or long or ulong or float or double or decimal or Guid or DateTime => v,
        byte[] => null,
        string[] arr => Cap(string.Join(", ", arr.Take(32))),
        _ => Cap(v.ToString() ?? "")
    };

    private static string Cap(string s) => s.Length > MaxString ? s[..MaxString] : s;

    /// <summary>The full message for one record (re-queried by record id). Never returns null.</summary>
    public static string FormatMessage(RawEvent ev)
    {
        var q = new EventLogQuery(ev.Log, PathType.LogName,
            "*[System[EventRecordID=" + ev.RecordId.ToString(CultureInfo.InvariantCulture) + "]]");
        using var reader = new EventLogReader(q);
        using var r = reader.ReadEvent();
        if (r == null) return $"This event is no longer in the {ev.Log} log (it may have been cleared or overwritten since).";
        string? msg = null;
        try { msg = r.FormatDescription(); }
        catch (EventLogException) { }
        if (string.IsNullOrWhiteSpace(msg))
        {
            var sb = new StringBuilder("Windows couldn't find the description for this event (the program that logged it may have been removed).");
            try
            {
                var p = r.Properties;
                if (p.Count > 0)
                {
                    sb.Append("\nEvent data:");
                    for (int i = 0; i < p.Count && i < MaxProps; i++)
                        sb.Append("\n  ").Append(i + 1).Append(": ").Append(Val.Str(Sanitize(p[i].Value)) ?? "");
                }
            }
            catch (EventLogException) { }
            msg = sb.ToString();
        }
        msg = msg.Replace("\r\n", "\n").Trim();
        return msg.Length > MaxMessage ? msg[..MaxMessage] + "…" : msg;
    }

    /// <summary>Newest record id in a log (0 if empty): the high-water mark for a read pass.</summary>
    public static long NewestRecordId(string log)
    {
        var q = new EventLogQuery(log, PathType.LogName, "*") { ReverseDirection = true };
        using var reader = new EventLogReader(q);
        using var r = reader.ReadEvent();
        return r?.RecordId ?? 0;
    }

    /// <summary>Oldest record number in the log (null if unknown) – it only ever grows unless the log is cleared.</summary>
    public static long? OldestRecordNumber(string log)
    {
        var info = EventLogSession.GlobalSession.GetLogInformation(log, PathType.LogName);
        return info.OldestRecordNumber;
    }
}
