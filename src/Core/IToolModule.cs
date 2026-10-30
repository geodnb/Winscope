using System.Windows;

namespace WinScope.Core;

/// <summary>
/// One of WinScope's tools. The shell shows <see cref="Summary"/> inside a card on the Home dashboard and
/// <see cref="FullView"/> when the tool is opened. Background collection starts in <see cref="Start"/> and keeps
/// running while the tool is hidden (so history is there when you look); views should skip expensive UI
/// refreshes while not visible.
/// All members are called on the UI thread.
/// </summary>
public interface IToolModule : IDisposable
{
    /// <summary>Section id from <see cref="Sections"/>.</summary>
    string Id { get; }
    string Title { get; }
    /// <summary>One line under the title on the dashboard card, e.g. "What's been using your PC".</summary>
    string Tagline { get; }
    /// <summary>A short glyph for the card and nav (Segoe Fluent/MDL2 or plain Unicode).</summary>
    string Icon { get; }

    /// <summary>Dashboard card body. Keep it compact; clicks outside buttons open the full view.</summary>
    FrameworkElement Summary { get; }
    FrameworkElement FullView { get; }

    /// <summary>Begin background work. Called once, after the main window has loaded.</summary>
    void Start();

    /// <summary>Visibility hint: whether the summary (Home) and/or the full view are on screen.</summary>
    void SetVisibility(bool summaryVisible, bool fullViewVisible);

    /// <summary>Raised by the summary (e.g. a "Review cleanup" button) to ask the shell to open the full view.</summary>
    event Action? OpenRequested;

    /// <summary>
    /// Closing: signal all background work to stop now, without waiting. The shell calls this on every module
    /// first (so they wind down in parallel), then <see cref="IDisposable.Dispose"/> on each, which may wait briefly.
    /// </summary>
    void BeginStop() { }

    /// <summary>
    /// Non-null while the tool is doing something that shouldn't be interrupted by a restart (e.g. a cleanup in
    /// progress) – a short reason like "a cleanup is running". The shell won't restart as admin while set.
    /// </summary>
    string? BusyReason => null;
}
