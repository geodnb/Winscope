using System.Text.Json;

namespace WinScope.Core;

public sealed class AppSettings
{
    public string LastSection { get; set; } = "home";
    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool Maximised { get; set; }

    public static string Folder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinScope");

    public static AppSettings Load()
    {
        try
        {
            if (UserFiles.TryRead("settings.json") is string json)
            {
                var s = JsonSerializer.Deserialize<AppSettings>(json);
                if (s != null) { s.Clamp(); return s; }
            }
        }
        catch { /* corrupt settings → defaults */ }
        return new AppSettings();
    }

    public void Clamp()
    {
        if (!Sections.IsValid(LastSection)) LastSection = Sections.Home;
        if (WindowWidth is double w && (double.IsNaN(w) || w < 900 || w > 10000)) WindowWidth = null;
        if (WindowHeight is double h && (double.IsNaN(h) || h < 600 || h > 10000)) WindowHeight = null;
    }

    /// <summary>Best effort; skipped while elevated (see <see cref="UserFiles"/>).</summary>
    public void Save() => UserFiles.TryWriteAllText("settings.json", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
}

/// <summary>Section ids used for navigation and the "--section=" restart argument.</summary>
public static class Sections
{
    public const string Home = "home", Performance = "performance", Network = "network", Events = "events", Storage = "storage";
    public static readonly string[] All = { Home, Performance, Network, Events, Storage };
    public static bool IsValid(string? s) => s != null && All.Contains(s);
}
