using System.Text.Json;
using WinScope.Core;

namespace WinScope.Modules.Events;

/// <summary>
/// The Events view's own small settings (events.json in the WinScope folder), read and written only through
/// <see cref="UserFiles"/>. When WinScope runs elevated nothing is written: changes last for that session only.
/// </summary>
internal sealed class EventsPrefs
{
    private const string FileName = "events.json";

    public int RangeDays { get; set; } = 7;
    public bool ShowInfo { get; set; }
    public bool ShowHarmless { get; set; }

    public static EventsPrefs Load()
    {
        try
        {
            var text = UserFiles.TryRead(FileName);
            if (text != null && text.Length < 16 * 1024 && JsonSerializer.Deserialize<EventsPrefs>(text) is EventsPrefs p)
            {
                if (p.RangeDays is not (1 or 7 or 30)) p.RangeDays = 7;
                return p;
            }
        }
        catch { /* corrupt → defaults */ }
        return new EventsPrefs();
    }

    public void Save()
    {
        try { UserFiles.TryWriteAllText(FileName, JsonSerializer.Serialize(this)); }
        catch { /* best effort */ }
    }
}
