using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;

namespace WinScope.Core;

/// <summary>
/// WinScope runs as a normal user. A few features (per-app network speeds, system cache cleanup) need
/// administrator rights; those offer to restart WinScope elevated, reopening the same section.
/// </summary>
public static class Elevation
{
    public static bool IsAdmin { get; } = CheckAdmin();

    /// <summary>
    /// False when this (elevated) copy is running as a different account from the person signed in to the
    /// desktop – e.g. someone typed another admin's password at the UAC prompt, or Windows 11 "Administrator
    /// protection" runs elevated apps under a separate hidden account. Per-user things (your temp files, browser
    /// caches, Recycle Bin, Downloads) would then belong to that other account, so tools should leave them alone.
    /// </summary>
    public static bool IsDesktopUser { get; } = CheckDesktopUser();

    /// <summary>"DOMAIN\name" of this process's account, for messages.</summary>
    public static string AccountName { get; } = SafeName();

    /// <summary>Set by the shell: returns a reason (e.g. "a cleanup is running") when a restart would interrupt work.</summary>
    public static Func<string?>? BusyCheck { get; set; }

    private static bool CheckAdmin()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static string SafeName()
    {
        try { using var id = WindowsIdentity.GetCurrent(); return id.Name; } catch { return ""; }
    }

    private static bool CheckDesktopUser()
    {
        try
        {
            string? user = WtsString(WTS_INFO_CLASS_UserName), domain = WtsString(WTS_INFO_CLASS_DomainName);
            // Can't tell who's at the desktop: harmless when not elevated; when elevated, fail safe (treat as another
            // account, which only turns off cleanup of personal folders).
            if (string.IsNullOrEmpty(user)) return !CheckAdmin();
            string desk = string.IsNullOrEmpty(domain) ? user : domain + "\\" + user;
            return string.Equals(desk, SafeName(), StringComparison.OrdinalIgnoreCase);
        }
        catch { return !CheckAdmin(); }
    }

    private const int WTS_CURRENT_SESSION = -1, WTS_INFO_CLASS_UserName = 5, WTS_INFO_CLASS_DomainName = 7;

    [DllImport("wtsapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool WTSQuerySessionInformationW(IntPtr hServer, int sessionId, int infoClass, out IntPtr buffer, out int bytes);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr memory);

    private static string? WtsString(int cls)
    {
        if (!WTSQuerySessionInformationW(IntPtr.Zero, WTS_CURRENT_SESSION, cls, out var p, out int bytes) || p == IntPtr.Zero) return null;
        try { return bytes >= 2 ? Marshal.PtrToStringUni(p, Math.Max(0, bytes / 2 - 1)).TrimEnd('\0') : ""; }
        finally { WTSFreeMemory(p); }
    }

    /// <summary>Result of a restart attempt: null on success (this copy is closing), otherwise why not.</summary>
    public static string? RestartAsAdmin(string section)
    {
        if (BusyCheck?.Invoke() is string busy) return $"Can't restart right now – {busy}. Try again when it's finished.";
        if (!Sections.IsValid(section)) section = Sections.Home;
        string? exe = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exe) || !File.Exists(exe) ||
            !string.Equals(Path.GetFileNameWithoutExtension(exe), "WinScope", StringComparison.OrdinalIgnoreCase))
            return "Can't restart as administrator from here – start WinScope.exe directly and try again.";
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                Verb = "runas",
                // Both values come from fixed lists, never from user input or files.
                Arguments = "--section=" + section + " --after-restart",
                WorkingDirectory = Path.GetDirectoryName(exe) ?? ""
            });
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return "Restart as administrator was cancelled."; }
        catch (Exception ex) { return "Couldn't restart as administrator: " + ex.Message; }
        System.Windows.Application.Current?.Shutdown();
        return null;
    }
}
