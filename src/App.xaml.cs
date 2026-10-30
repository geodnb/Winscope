using System.Windows;
using System.Windows.Threading;
using WinScope.Core;

namespace WinScope;

public partial class App : Application
{
    private bool _shown;
    private bool _started;
    private static volatile bool _closing;
    private Mutex? _single;

    public static string ErrorLogPath => Path.Combine(AppSettings.Folder, "error.log");

    /// <summary>Set once the main window starts closing, so late errors are only logged.</summary>
    public static bool Closing { get => _closing; set => _closing = value; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, a) => { if (a.ExceptionObject is Exception ex) Log(ex); };

        // One WinScope per signed-in user: two copies would double the sampling and could run two cleanups.
        // After "Restart as admin" the old copy is still closing, so wait for it briefly.
        bool afterRestart = e.Args.Contains("--after-restart");
        if (!AcquireSingleInstance(afterRestart ? 8000 : 0))
        {
            MessageBox.Show("WinScope is already running – check the taskbar.", "WinScope", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        try
        {
            // "--section=storage" etc. is passed when WinScope restarts itself as administrator.
            string? section = e.Args.FirstOrDefault(a => a.StartsWith("--section=", StringComparison.Ordinal))?["--section=".Length..];
            var w = new MainWindow(section);
            MainWindow = w;
            w.Show();
            _started = true;
        }
        catch (Exception ex)
        {
            Fatal(ex);
        }
    }

    private bool AcquireSingleInstance(int waitMs)
    {
        try
        {
            string sid;
            try { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); sid = id.User?.Value ?? "user"; }
            catch { sid = "user"; }
            // Per session and per desktop user (an elevated copy as the same user shares it; a different account doesn't).
            string user = Elevation.IsDesktopUser ? sid : "other-" + sid;
            _single = new Mutex(false, @"Local\WinScope-" + user);
            try { return _single.WaitOne(waitMs); }
            catch (AbandonedMutexException) { return true; }   // previous copy crashed – fine
        }
        // An elevated copy created it with admin-only access: WinScope is already running (as administrator).
        catch (UnauthorizedAccessException) { return false; }
        catch { return true; }   // can't tell – don't block startup
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _single?.ReleaseMutex(); } catch { }
        _single?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        e.Handled = true;
        Log(e.Exception);
        if (_closing) return;   // errors while shutting down: log only
        // If the main window never got on screen, don't leave an invisible process running.
        if (!_started || MainWindow is not { IsVisible: true } && MainWindow?.WindowState != WindowState.Minimized)
        {
            Fatal(e.Exception);
            return;
        }
        if (_shown) return;
        _shown = true;
        MessageBox.Show("Something went wrong in the UI:\n\n" + Describe(e.Exception) +
                        (UserFiles.WriteAllowed ? $"\n\nWinScope will keep running. Details were saved to:\n{ErrorLogPath}" : "\n\nWinScope will keep running."),
            "WinScope", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void Fatal(Exception ex)
    {
        Log(ex);
        MessageBox.Show("WinScope couldn't start:\n\n" + Describe(ex) +
                        (UserFiles.WriteAllowed ? $"\n\nDetails were saved to:\n{ErrorLogPath}" : ""),
            "WinScope", MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(1);
    }

    /// <summary>Show the innermost cause – "type initializer threw" on its own is useless.</summary>
    private static string Describe(Exception ex)
    {
        var root = ex.GetBaseException();
        return root == ex ? ex.Message : $"{ex.Message}\n→ {root.GetType().Name}: {root.Message}";
    }

    /// <summary>Record an error without bothering the user (modules use this for recoverable failures). Thread-safe.</summary>
    public static void LogError(Exception ex) => Log(ex);

    private static readonly object LogLock = new();
    private static readonly Dictionary<string, (DateTime last, int suppressed)> Recent = new();

    /// <summary>
    /// Thread-safe, size-capped (via <see cref="UserFiles"/>), and de-duplicated: the same error from the same place
    /// is written at most once a minute, with a count of how many were skipped. Not written while elevated.
    /// </summary>
    private static void Log(Exception ex)
    {
        try
        {
            string key = ex.GetType().FullName + "|" + ex.Message + "|" + (ex.StackTrace?.Split('\n').FirstOrDefault() ?? "");
            int skipped;
            lock (LogLock)
            {
                var now = DateTime.UtcNow;
                if (Recent.TryGetValue(key, out var r) && (now - r.last).TotalSeconds < 60)
                {
                    Recent[key] = (r.last, r.suppressed + 1);
                    return;
                }
                skipped = Recent.TryGetValue(key, out r) ? r.suppressed : 0;
                Recent[key] = (now, 0);
                if (Recent.Count > 500) Recent.Clear();
            }
            UserFiles.TryAppend("error.log",
                $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · WinScope {typeof(App).Assembly.GetName().Version} · {Environment.OSVersion}" +
                (skipped > 0 ? $" · (same error {skipped} more time{(skipped == 1 ? "" : "s")} in the last minute)" : "") + $"\n{ex}\n\n");
        }
        catch { }
    }
}
