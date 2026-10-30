using System.ComponentModel;
using System.Globalization;
using System.Text;
using System.Windows.Media;
using WinScope.UI;

namespace WinScope.Modules.Events;

/// <summary>A day heading in the timeline ("Today", "Yesterday", "Monday 26 October").</summary>
public sealed class DayHeaderVm : INotifyPropertyChanged
{
    private string _count = "";
    public DayHeaderVm(DateTime date) { Date = date; }
    public DateTime Date { get; }
    public string Text
    {
        get
        {
            var today = DateTime.Now.Date;
            if (Date == today) return "Today";
            if (Date == today.AddDays(-1)) return "Yesterday";
            return Date.ToString("dddd d MMMM", CultureInfo.InvariantCulture);
        }
    }
    public string CountText
    {
        get => _count;
        set { if (_count != value) { _count = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CountText))); } }
    }
    public void RefreshText() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One timeline item (a collapsed group). UI thread only. Getters that need the formatted message ask the engine
/// for it – bindings only evaluate them when the row is realised, so with virtualisation only on-screen items are
/// formatted. Code that filters or searches uses the plain properties, which never trigger a load.
/// </summary>
public sealed class EventItemVm : INotifyPropertyChanged
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly EventsEngine _engine;
    private bool _expanded;
    private string? _search;

    internal EventItemVm(EventGroup g, EventsEngine engine)
    {
        Group = g;
        _engine = engine;
    }

    public EventGroup Group { get; private set; }
    public Classified Newest => Group.Newest;
    public string MessageKey => Group.Newest.Ev.Key;
    public DateTime NewestUtc => Group.Newest.TimeUtc;
    public DateTime NewestLocal => Group.Newest.TimeUtc.ToLocalTime();

    internal void Update(EventGroup g)
    {
        if (ReferenceEquals(g, Group)) return;
        Group = g;
        _search = null;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));   // everything may have changed
    }

    internal void OnMessageReady()
    {
        _search = null;
        Raise(nameof(Title));
        Raise(nameof(WindowsSays));
        Raise(nameof(HasWindowsSays));
        Raise(nameof(DetailsText));
    }

    /// <summary>Called when a time-relative label (day header, "Ago") should be refreshed.</summary>
    internal void RefreshTimes() => Raise(nameof(TimeText));

    private string? Message(bool request)
    {
        var m = _engine.GetMessage(MessageKey);
        if (m == null && request) _engine.RequestMessage(Group.Newest.Ev);
        return m;
    }

    // ------------------------------------------------------------------ display

    /// <summary>Title without triggering a message load (for the dashboard, search and copy).</summary>
    public string PlainTitle
    {
        get
        {
            var c = Newest;
            if (c.MessageIsTitle && Message(false) is string m && MessageText.FirstLine(m) is { Length: > 0 } line)
                return c.Ev.Provider + ": " + line;
            return c.Title;
        }
    }

    public string Title
    {
        get
        {
            var c = Newest;
            if (!c.MessageIsTitle) return c.Title;
            var m = Message(true);
            if (m != null && MessageText.FirstLine(m) is { Length: > 0 } line) return c.Ev.Provider + ": " + line;
            return c.Title;
        }
    }

    public bool HasWindowsSays => Newest.ShowMessageLine && _engine.GetMessage(MessageKey) is string m && MessageText.Summary(m, Newest.MessageHint).Length > 0;

    public string? WindowsSays
    {
        get
        {
            if (!Newest.ShowMessageLine) return null;
            var m = Message(true);
            if (m == null) return null;
            var s = MessageText.Summary(m, Newest.MessageHint);
            return s.Length == 0 ? null : "Windows says: " + s;
        }
    }

    public string Explain => Newest.Explain;
    public bool HasTips => Newest.Tips.Count > 0;
    public string TipsText => string.Join("\n", Newest.Tips.Select(t => "•  " + t));

    public string TimeText
    {
        get
        {
            var newest = NewestLocal;
            if (Group.Count == 1) return newest.ToString("HH:mm", Inv);
            var oldest = Group.Oldest.TimeUtc.ToLocalTime();
            return oldest.ToString("HH:mm", Inv) == newest.ToString("HH:mm", Inv)
                ? newest.ToString("HH:mm", Inv)
                : oldest.ToString("HH:mm", Inv) + "–\n" + newest.ToString("HH:mm", Inv);
        }
    }

    public bool HasCount => Group.Count > 1;
    public string CountText => "×" + Group.Count.ToString(Inv);
    public bool IsHarmless => Newest.Harmless;
    public double RowOpacity => Newest.Harmless ? 0.72 : 1.0;
    public string CategoryText => CategoryName(Newest.Category);

    public static string CategoryName(EvCategory c) => EvText.CategoryName(c);

    public Brush SevBrush => Styles.SevBrush(Newest);
    public Brush SevSoft => Styles.SevSoft(Newest);
    public string Glyph => Styles.Glyph(Newest);

    public bool IsExpanded
    {
        get => _expanded;
        set
        {
            if (_expanded == value) return;
            _expanded = value;
            if (value) Message(true);
            Raise(nameof(IsExpanded));
            Raise(nameof(ExpandText));
            Raise(nameof(DetailsText));
        }
    }

    public string ExpandText => _expanded ? "Hide details" : "Show details  ›";

    /// <summary>The raw details block (selectable and copyable in the view).</summary>
    public string DetailsText
    {
        get
        {
            if (!_expanded) return "";
            var c = Newest;
            var e = c.Ev;
            var sb = new StringBuilder();
            sb.Append("When:     ").Append(NewestLocal.ToString("ddd d MMM yyyy HH:mm:ss", Inv));
            if (Group.Count > 1)
            {
                sb.Append("\nRepeats:  ").Append(Group.Count.ToString(Inv)).Append(" times – ");
                sb.Append(string.Join(", ", Group.Items.Take(12).Select(i => i.TimeUtc.ToLocalTime().ToString("HH:mm:ss", Inv))));
                if (Group.Count > 12) sb.Append(", …");
            }
            sb.Append("\nLog:      ").Append(e.Log);
            sb.Append("\nSource:   ").Append(e.Provider);
            sb.Append("\nEvent ID: ").Append(e.Id.ToString(Inv));
            sb.Append("\nLevel:    ").Append(e.LevelName);
            sb.Append("\nRecord:   ").Append(e.RecordId.ToString(Inv));
            sb.Append("\n\n");
            var m = Message(true);
            sb.Append(m ?? "Loading the full message…");
            if (Group.Related.Count > 0)
            {
                sb.Append("\n\nAlso logged for the same incident:");
                foreach (var r in Group.Related.Take(20))
                    sb.Append("\n•  ").Append(r.TimeUtc.ToLocalTime().ToString("HH:mm:ss", Inv)).Append("  ")
                      .Append(r.Ev.Provider).Append(' ').Append(r.Ev.Id.ToString(Inv)).Append(" – ").Append(r.Title);
            }
            return sb.ToString();
        }
    }

    /// <summary>Everything worth copying for a forum post or support ticket.</summary>
    public string CopyText()
    {
        bool was = _expanded;
        _expanded = true;
        string details = DetailsText;
        _expanded = was;
        var sb = new StringBuilder();
        sb.Append(PlainTitle).Append('\n').Append(Explain);
        if (HasTips) sb.Append("\n\nWhat you can try:\n").Append(TipsText);
        sb.Append("\n\n").Append(details);
        return sb.ToString();
    }

    /// <summary>Lower-case text for the search box (no message load).</summary>
    public bool Matches(string lowerQuery)
    {
        _search ??= BuildSearch();
        return _search.Contains(lowerQuery, StringComparison.Ordinal);
    }

    private string BuildSearch()
    {
        var c = Newest;
        var sb = new StringBuilder();
        sb.Append(PlainTitle).Append('\n').Append(c.Explain).Append('\n').Append(c.Ev.Provider).Append('\n')
          .Append(c.Ev.Id.ToString(Inv)).Append('\n').Append(CategoryText).Append('\n').Append(c.Subject);
        if (_engine.GetMessage(MessageKey) is string m) sb.Append('\n').Append(m);
        return sb.ToString().ToLowerInvariant();
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>Severity colours and glyphs shared by the full view and the dashboard card.</summary>
internal static class Styles
{
    private static readonly Brush Critical = Palette.B(Palette.Bad), CriticalSoft = Palette.B(Palette.Bad, 0.16);
    private static readonly Brush Problem = Palette.B(Palette.Weak), ProblemSoft = Palette.B(Palette.Weak, 0.16);
    private static readonly Brush Minor = Palette.B(Palette.Fair), MinorSoft = Palette.B(Palette.Fair, 0.13);
    private static readonly Brush Info = Palette.B(Palette.Accent2), InfoSoft = Palette.B(Palette.Accent2, 0.14);
    private static readonly Brush Muted = Palette.B(Palette.Muted), MutedSoft = Palette.B(Palette.Muted, 0.14);

    public static Brush SevBrush(Classified c) => c.Harmless ? Muted : c.Severity switch
    {
        EvSeverity.Critical => Critical,
        EvSeverity.Problem => Problem,
        EvSeverity.Minor => Minor,
        _ => Info,
    };

    public static Brush SevSoft(Classified c) => c.Harmless ? MutedSoft : c.Severity switch
    {
        EvSeverity.Critical => CriticalSoft,
        EvSeverity.Problem => ProblemSoft,
        EvSeverity.Minor => MinorSoft,
        _ => InfoSoft,
    };

    public static string Glyph(Classified c) => c.Harmless ? "i" : c.Severity switch
    {
        EvSeverity.Critical => "!",
        EvSeverity.Problem => "!",
        EvSeverity.Minor => "?",
        _ => "i",
    };

    /// <summary>Counts as a "problem" on the dashboard.</summary>
    public static bool IsProblem(Classified c) => EvText.IsProblem(c);
}
