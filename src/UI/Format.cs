using System.Globalization;

namespace WinScope.UI;

public static class Format
{
    private static readonly CultureInfo C = CultureInfo.InvariantCulture;

    /// <summary>1,536 → "1.5 KB" (binary units, labelled as Windows does).</summary>
    public static string Bytes(double b)
    {
        if (double.IsNaN(b) || b < 0) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        int i = 0;
        // Step up a unit once the rounded value would show as 1024 or more (so 1023.6 KB reads "1.00 MB", not "1024 KB").
        while (b >= 1023.5 && i < u.Length - 1) { b /= 1024; i++; }
        return i == 0 ? $"{b.ToString("0", C)} {u[i]}" : $"{b.ToString(b >= 100 ? "0" : b >= 10 ? "0.0" : "0.00", C)} {u[i]}";
    }

    public static string Rate(double bytesPerSec) => bytesPerSec < 1 ? "0 B/s" : Bytes(bytesPerSec) + "/s";

    public static string Pct(double v, int decimals = 0) =>
        double.IsNaN(v) ? "—" : v.ToString(decimals == 0 ? "0" : "0." + new string('0', decimals), C) + "%";

    public static string Span(TimeSpan t) =>
        t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h" :
        t.TotalHours >= 1 ? $"{(int)t.TotalHours}h {t.Minutes}m" :
        t.TotalMinutes >= 1 ? $"{t.Minutes}m {t.Seconds}s" : $"{Math.Max(0, t.Seconds)}s";

    /// <summary>"just now", "5 min ago", "3 h ago", "Tue 14:05", "12 Oct".</summary>
    public static string Ago(DateTime local)
    {
        var d = DateTime.Now - local;
        if (d.TotalSeconds < 60) return "just now";
        if (d.TotalMinutes < 60) return $"{(int)d.TotalMinutes} min ago";
        if (d.TotalHours < 24) return $"{(int)d.TotalHours} h ago";
        if (d.TotalDays < 7) return local.ToString("ddd HH:mm", C);
        return local.ToString("d MMM", C);
    }
}
