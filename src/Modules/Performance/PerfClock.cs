using System.Diagnostics;

namespace WinScope.Modules.Performance;

/// <summary>
/// The tool's timeline clock. Samples are stamped from a monotonic Stopwatch anchored to UTC once at start, so a
/// wall-clock change (manual change, a big NTP correction) can never put the history out of order or wipe it.
/// The clock is re-synced against the real wall clock every second:
///  • wall clock ahead by more than 2 s (resume from sleep, clock set forwards): the timeline jumps forwards –
///    a gap appears, and earlier samples keep their labels;
///  • wall clock behind by more than 2 s (clock set backwards): stamps keep increasing and only the *display*
///    offset moves back, so labels match the wall clock again without touching the data.
/// All UI code converts stamps to wall/local time with <see cref="ToWall"/> / <see cref="ToLocal"/>.
/// </summary>
internal static class PerfClock
{
    private static readonly long AnchorUtcTicks = DateTime.UtcNow.Ticks;
    private static readonly long AnchorStamp = Stopwatch.GetTimestamp();
    private static long _forwardTicks;      // added to the monotonic time (only ever grows)
    private static long _displayTicks;      // stamp + display = wall-clock UTC (≤ 0 after backward steps)
    public static readonly long Tolerance = 2 * TimeSpan.TicksPerSecond;

    private static long MonoTicks()
    {
        long el = Stopwatch.GetTimestamp() - AnchorStamp;
        return AnchorUtcTicks + (long)(el * ((double)TimeSpan.TicksPerSecond / Stopwatch.Frequency));
    }

    /// <summary>Now, on the timeline (UTC kind, but may differ from DateTime.UtcNow after a clock change).</summary>
    public static DateTime Now => new(MonoTicks() + Interlocked.Read(ref _forwardTicks), DateTimeKind.Utc);

    public static DateTime ToWall(DateTime stamp) => new(stamp.Ticks + Interlocked.Read(ref _displayTicks), DateTimeKind.Utc);
    public static DateTime ToLocal(DateTime stamp) => ToWall(stamp).ToLocalTime();
    /// <summary>Wall-clock UTC → timeline stamp (inverse of <see cref="ToWall"/>).</summary>
    public static DateTime FromWall(DateTime wallUtc) => new(wallUtc.Ticks - Interlocked.Read(ref _displayTicks), DateTimeKind.Utc);

    /// <summary>Re-sync with the wall clock (called by the sampler each tick). Returns true if anything moved.</summary>
    public static bool Resync()
    {
        long fwd = Interlocked.Read(ref _forwardTicks), disp = Interlocked.Read(ref _displayTicks);
        bool moved = Adjust(ref fwd, ref disp, DateTime.UtcNow.Ticks, MonoTicks());
        if (moved)
        {
            Interlocked.Exchange(ref _forwardTicks, fwd);
            Interlocked.Exchange(ref _displayTicks, disp);
        }
        return moved;
    }

    /// <summary>Pure core of <see cref="Resync"/> (unit-tested).</summary>
    public static bool Adjust(ref long forward, ref long display, long wallTicks, long monoTicks)
    {
        long stamp = monoTicks + forward;
        long err = wallTicks - (stamp + display);
        if (err > Tolerance)
        {
            // First undo any earlier backward display shift (clock set back, then forward again: no fake gap)…
            long take = Math.Min(err, -Math.Min(0, display));
            display += take;
            err -= take;
            forward += err;                                          // …then the timeline jumps forwards; order kept
            return true;
        }
        if (err < -Tolerance) { display += err; return true; }      // labels move back; data untouched
        return false;
    }

    /// <summary>
    /// Axis ticks at round LOCAL clock times between two wall-clock UTC instants. Each tick is converted to local
    /// time on its own, so labels stay right across a daylight-saving change inside the window (and in time zones
    /// with half-hour offsets). Output: (wall-clock UTC ticks, local ticks).
    /// </summary>
    public static void AxisTicks(DateTime wallStartUtc, DateTime wallEndUtc, long stepTicks, TimeZoneInfo tz,
        List<(long utc, long local)> output)
    {
        output.Clear();
        if (stepTicks <= 0 || wallEndUtc <= wallStartUtc) return;
        long u = wallStartUtc.Ticks, end = wallEndUtc.Ticks;
        for (int guard = 0; guard < 4000 && u <= end; guard++)
        {
            long local;
            try { local = TimeZoneInfo.ConvertTimeFromUtc(new DateTime(u, DateTimeKind.Utc), tz).Ticks; }
            catch (ArgumentException) { return; }
            long rem = ((local % stepTicks) + stepTicks) % stepTicks;
            if (rem != 0) { u += stepTicks - rem; continue; }      // align (again, if the offset just changed)
            output.Add((u, local));
            u += stepTicks;
        }
    }
}
