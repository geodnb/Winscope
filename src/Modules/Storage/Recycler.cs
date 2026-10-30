using Microsoft.Win32;
using WinScope.Core;

namespace WinScope.Modules.Storage;

/// <summary>What the user is asked to confirm: the exact file (as Windows names it) and anything worth warning about.</summary>
internal sealed record RecyclePlan(string FinalRaw, long Size, bool OneDrive, FileId Id);

/// <summary>Moves single files from the analyser to the Recycle Bin, after re-checking them twice.</summary>
internal static class Recycler
{
    /// <summary>The longest path the shell handles reliably (MAX_PATH minus the terminator).</summary>
    private const int ShellMaxPath = 259;
    private const FileAttributes Pinned = (FileAttributes)0x00080000, Unpinned = (FileAttributes)0x00100000;   // cloud sync states

    /// <summary>Non-null when moves aren't offered at all in this copy of WinScope.</summary>
    public static string? WhyUnavailable =>
        // Elevated, a path could be swapped between our check and the shell's move (the shell follows parent
        // junctions), turning a Recycle Bin move into an admin-powered move of something else. Not worth it.
        Elevation.IsAdmin ? "Recycle Bin moves are only available when WinScope isn't running as administrator."
        : !AccountGuard.PersonalAllowed ? AccountGuard.Note
        : null;

    /// <summary>First check, before asking the user. Never throws.</summary>
    public static Task<(RecyclePlan? plan, string? why)> PrepareAsync(BigFile f) => Task.Run(() => SafeCheck(f));

    /// <summary>
    /// Second check right before the move; it must find exactly the confirmed file. Returns null on success,
    /// otherwise a friendly reason. Never throws.
    /// </summary>
    public static async Task<string?> MoveAsync(BigFile f, RecyclePlan confirmed, IntPtr hwnd)
    {
        try
        {
            var (plan, why) = await Task.Run(() => SafeCheck(f)).ConfigureAwait(false);
            if (why != null || plan == null) return why ?? "It couldn't be checked.";
            if (!string.Equals(plan.FinalRaw, confirmed.FinalRaw, StringComparison.Ordinal) || plan.Size != confirmed.Size || plan.Id != confirmed.Id)
                return "It has moved or changed since you confirmed – nothing was removed. Scan again first.";

            var (outcome, hr) = await StorageNative.RunSta(() => StorageNative.RecycleWithShell(plan.FinalRaw, plan.Id, hwnd)).ConfigureAwait(false);
            bool gone = await Task.Run(() => StorageNative.IsReallyGone(plan.FinalRaw)).ConfigureAwait(false);
            return outcome switch
            {
                StorageNative.RecycleOutcome.Recycled => gone ? null : "Windows said it was moved, but the file is still there – it may be in use.",
                StorageNative.RecycleOutcome.Vetoed => gone
                    ? "Windows removed it without the Recycle Bin. Sorry – check the Recycle Bin; it may not be there."
                    : "Windows would have deleted it for good instead of using the Recycle Bin, so WinScope stopped. Nothing was removed.",
                StorageNative.RecycleOutcome.DeletedNotRecycled => gone
                    ? "Windows deleted it without putting it in the Recycle Bin."
                    : "Windows didn't move it – nothing was removed. It may be in use.",
                StorageNative.RecycleOutcome.Cancelled => "Cancelled – the file wasn't removed.",
                _ => $"Windows couldn't move it to the Recycle Bin (0x{hr:X8}). It may be in use.",
            };
        }
        catch (Exception ex)
        {
            App.LogError(ex);
            return "Something went wrong: " + ex.Message;
        }
    }

    private static (RecyclePlan?, string?) SafeCheck(BigFile f)
    {
        try { return Check(f); }
        catch (Exception ex) { App.LogError(ex); return (null, "It couldn't be checked: " + ex.Message); }
    }

    /// <summary>All the checks, done right before asking and again right before the move.</summary>
    internal static (RecyclePlan? plan, string? why) Check(BigFile f)
    {
        if (WhyUnavailable is string u) return (null, u);
        // A name ending in '.' or ' ' would be trimmed by path APIs, acting on a sibling ("movie.mkv " → "movie.mkv").
        if (WinPath.HasTrailingDotOrSpaceSegment(f.Path)) return (null, "Its name ends in a dot or space, which Windows tools handle unreliably – use Explorer instead.");
        string full;
        try { full = Path.GetFullPath(f.Path); } catch { return (null, "That path isn't valid."); }
        string? norm = WinPath.Normalize(full);
        if (SafetyRules.WhyProtected(norm, SafetyContext.Protected) is string w1) return (null, $"WinScope won't remove this: {w1}.");

        FileFacts facts;
        string? finalRaw;
        FileId? fileId;
        using (var h = StorageNative.Open(full, forDelete: false, listDirectory: false, out int err))
        {
            if (h.IsInvalid)
            {
                if (StorageNative.IsGone(err)) return (null, "It's no longer there.");
                if (err is StorageNative.ERROR_SHARING_VIOLATION or StorageNative.ERROR_LOCK_VIOLATION) return (null, "It's in use by another app.");
                return (null, $"Windows wouldn't let WinScope check it (error {err}).");
            }
            if (!StorageNative.TryGetFacts(h, out facts)) return (null, "Couldn't read its details.");
            finalRaw = StorageNative.FinalPathRaw(h);
            fileId = StorageNative.TryGetFileId(h);
        }
        if (fileId is not FileId id) return (null, "Windows couldn't identify the file, so WinScope won't move it.");
        if (SafetyRules.WhyProtected(norm, SafetyContext.Protected, facts.Attributes) is string w2) return (null, $"WinScope won't remove this: {w2}.");
        if (facts.IsDirectory) return (null, "It's a folder now, not a file.");
        if (facts.DeletePending) return (null, "It's already being deleted.");
        if (FileRules.IsCloudOnly(facts.Attributes)) return (null, "It's an online-only file – remove it in OneDrive instead.");
        if (facts.Size != f.Size || facts.WriteUtc != f.WriteUtc) return (null, "It has changed since the scan – scan again first.");

        // Where it really is – this exact string is what the shell gets (only "\\?\" removed, nothing trimmed).
        if (finalRaw == null) return (null, "Windows couldn't say where it really is.");
        if (WinPath.HasTrailingDotOrSpaceSegment(finalRaw)) return (null, "Part of its real location ends in a dot or space – use Explorer instead.");
        string? finalNorm = WinPath.Normalize(finalRaw);
        if (SafetyRules.WhyProtected(finalNorm, SafetyContext.Protected, facts.Attributes) is string w3) return (null, $"WinScope won't remove this: {w3}.");
        if (finalRaw.Length > ShellMaxPath) return (null, "Its path is too long for the Recycle Bin – use Explorer instead.");

        // Drive checks on the volume the file REALLY lives on – for a drive mounted in a folder that's the mounted
        // volume, not the drive letter above it: a fixed disk with a Recycle Bin big enough for this file.
        if (WinPath.IsUnc(finalNorm!)) return (null, "This drive has no Recycle Bin, so WinScope won't delete from it.");
        string? mount = StorageNative.VolumeMountPoint(finalRaw);
        if (mount == null || !StorageNative.IsFixedVolume(mount)) return (null, "This drive has no Recycle Bin, so WinScope won't delete from it.");
        long total = StorageNative.VolumeTotalBytes(mount);
        var (max, nuke) = BinSettings(mount);
        if (RecycleRules.WhyTooBig(facts.Size, max, nuke, total) is string big) return (null, $"WinScope won't move it: {big}.");

        bool oneDrive = SafetyRules.IsUnderAny(finalNorm, SafetyContext.OneDrive) || SafetyRules.IsUnderAny(norm, SafetyContext.OneDrive) ||
                        (facts.Attributes & (Pinned | Unpinned)) != 0;
        return (new RecyclePlan(finalRaw, facts.Size, oneDrive, id), null);
    }

    /// <summary>
    /// The Recycle Bin settings for a drive: HKCU\…\Explorer\BitBucket\Volume\{GUID} MaxCapacity (MB) and
    /// NukeOnDelete. Unknown values come back as (null, false) and the caller falls back to 5% of the drive.
    /// </summary>
    private static (long? maxBytes, bool nuke) BinSettings(string mountPoint)
    {
        try
        {
            string? vol = StorageNative.VolumeGuidPath(mountPoint);           // "\\?\Volume{GUID}\"
            if (vol == null) return (null, false);
            int a = vol.IndexOf('{'), b = vol.IndexOf('}');
            if (a < 0 || b <= a) return (null, false);
            string guid = vol[a..(b + 1)];
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Explorer\BitBucket\Volume\" + guid);
            if (key == null) return (null, false);
            bool nuke = key.GetValue("NukeOnDelete") is int n && n != 0;
            long? max = key.GetValue("MaxCapacity") is int mb && mb > 0 ? mb * 1024L * 1024L : null;
            return (max, nuke);
        }
        catch { return (null, false); }
    }
}
