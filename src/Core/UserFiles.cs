using System.Text;

namespace WinScope.Core;

/// <summary>
/// The only way WinScope writes its own files (settings, preferences, logs) to %AppData%\WinScope.
/// That folder belongs to the normal user, so anything running as that user could swap it for a link. An
/// elevated WinScope following such a link could be tricked into overwriting system files – so when running as
/// administrator, WinScope simply doesn't write there (settings changes last for that session only).
/// </summary>
public static class UserFiles
{
    private static readonly object Lock = new();

    /// <summary>False while elevated: callers should keep data in memory instead.</summary>
    public static bool WriteAllowed => !Elevation.IsAdmin;

    public static string Folder => AppSettings.Folder;

    /// <summary>Path of a file in the WinScope folder. <paramref name="name"/> must be a plain file name.</summary>
    public static string PathOf(string name)
    {
        if (name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.Contains("..")) throw new ArgumentException("Bad file name", nameof(name));
        return Path.Combine(Folder, name);
    }

    /// <summary>Write a whole file atomically (temp file + move). No-op when elevated. Never throws.</summary>
    public static bool TryWriteAllText(string name, string text)
    {
        if (!WriteAllowed) return false;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                string path = PathOf(name), tmp = path + ".tmp";
                File.WriteAllText(tmp, text, new UTF8Encoding(false));
                File.Move(tmp, path, true);
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Append to a log file, keeping it under <paramref name="maxBytes"/> (when exceeded, the file is restarted
    /// with the newest half kept). No-op when elevated. Never throws.
    /// </summary>
    public static bool TryAppend(string name, string text, long maxBytes = 1_000_000)
    {
        if (!WriteAllowed) return false;
        try
        {
            lock (Lock)
            {
                Directory.CreateDirectory(Folder);
                string path = PathOf(name);
                var fi = new FileInfo(path);
                if (fi.Exists && fi.Length > maxBytes)
                {
                    string all = File.ReadAllText(path);
                    File.WriteAllText(path, "…(older entries trimmed)\n" + all[(all.Length / 2)..], new UTF8Encoding(false));
                }
                File.AppendAllText(path, text, new UTF8Encoding(false));
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Read a file from the WinScope folder, or null. Reading is fine when elevated.</summary>
    public static string? TryRead(string name)
    {
        try { var p = PathOf(name); return File.Exists(p) ? File.ReadAllText(p) : null; }
        catch { return null; }
    }
}
