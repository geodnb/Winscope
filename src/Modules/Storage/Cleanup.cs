using WinScope.Core;
using WinScope.UI;

namespace WinScope.Modules.Storage;

/// <summary>
/// Measures and cleans the cleanup categories. Every root is checked against its expected parent (no links in
/// between, same real location), every folder is re-checked before it's listed, and every file is re-checked at
/// delete time through an open handle (no reparse points, real location inside the root – by path and by file id –
/// filter still matches) before it's marked for deletion. Emptied sub-folders are removed bottom-up, never the root.
/// </summary>
internal sealed class CleanupEngine
{
    public const string LogName = "cleanup-log.txt";
    private static readonly TimeSpan BinQueryTimeout = TimeSpan.FromSeconds(20);

    private readonly TaskBag _tasks;
    private readonly SemaphoreSlim _gate = new(1, 1);       // analysis phase 1 and cleaning never overlap
    private readonly object _lock = new();
    private readonly List<string> _sessionLog = new();
    private bool _analysing, _again, _reportsAnalysing, _cleaning;
    private Dictionary<string, CategoryResult> _results = new();
    private DateTime? _analysedUtc;
    private long? _safeBytes;
    private volatile EngineSnapshot _snap = new();

    public IReadOnlyList<CategoryDef> Defs { get; }
    public EngineSnapshot Snapshot => _snap;
    /// <summary>Raised on a background thread whenever <see cref="Snapshot"/> changes.</summary>
    public event Action? Changed;

    public static string LogPath => Path.Combine(UserFiles.Folder, LogName);

    /// <summary>Log lines written this session (all that exists of the log while elevated).</summary>
    public IReadOnlyList<string> SessionLog { get { lock (_lock) return _sessionLog.ToList(); } }

    public CleanupEngine(TaskBag tasks)
    {
        _tasks = tasks;
        Defs = Catalog.Build();
    }

    private void Publish()
    {
        lock (_lock)
        {
            _snap = new EngineSnapshot
            {
                Results = new Dictionary<string, CategoryResult>(_results), Analysing = _analysing,
                ReportsAnalysing = _reportsAnalysing, Cleaning = _cleaning, AnalysedUtc = _analysedUtc, SafeBytes = _safeBytes,
            };
        }
        try { Changed?.Invoke(); } catch (Exception ex) { App.LogError(ex); }
    }

    // ================================================================== analysis

    /// <summary>Measure every category in the background (coalesces repeated requests).</summary>
    public void Analyse()
    {
        lock (_lock)
        {
            if (_analysing) { _again = true; return; }
            _analysing = true;
            _again = false;
        }
        Publish();
        _tasks.Run(async ct =>
        {
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    await _gate.WaitAsync(ct).ConfigureAwait(false);
                    try { AnalyseFiles(ct); }
                    catch (OperationCanceledException) { }
                    catch (Exception ex) { App.LogError(ex); }
                    finally { _gate.Release(); }
                    lock (_lock)
                    {
                        if (!_again) { _analysing = false; break; }
                        _again = false;
                    }
                }
            }
            catch (OperationCanceledException) { }
            finally
            {
                lock (_lock) _analysing = false;
                Publish();
            }
            if (!ct.IsCancellationRequested) AnalyseReports(ct);
        });
    }

    private void AnalyseFiles(CancellationToken ct)
    {
        var now = DateTime.UtcNow;
        foreach (var def in Defs)
        {
            if (ct.IsCancellationRequested) return;
            if (def.ReportOnly) continue;
            CategoryResult r;
            try { r = def.Kind == CatKind.RecycleBin ? MeasureRecycleBin(def, ct) : MeasureCategory(def, now, ct); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                App.LogError(ex);
                r = new CategoryResult { Def = def, Notes = { "Couldn't check this: " + ex.Message } };
            }
            lock (_lock) _results[def.Id] = r;
            Publish();
        }
        if (ct.IsCancellationRequested) return;
        lock (_lock)
        {
            _analysedUtc = DateTime.UtcNow;
            _safeBytes = Defs.Where(d => d.DefaultOn && !d.ReportOnly)
                .Sum(d => _results.TryGetValue(d.Id, out var x) && x.Cleanable ? x.Bytes : 0);
        }
    }

    private void AnalyseReports(CancellationToken ct)
    {
        lock (_lock) { if (_reportsAnalysing) return; _reportsAnalysing = true; }
        Publish();
        try
        {
            foreach (var def in Defs.Where(d => d.ReportOnly))
            {
                if (ct.IsCancellationRequested) return;
                CategoryResult r;
                try { r = MeasureReport(def, ct); }
                catch (Exception ex) { App.LogError(ex); r = new CategoryResult { Def = def, Present = false, Notes = { "Couldn't check this." } }; }
                lock (_lock) _results[def.Id] = r;
                Publish();
            }
        }
        finally
        {
            lock (_lock) _reportsAnalysing = false;
            Publish();
        }
    }

    /// <summary>Recycle Bin size right now (STA, bounded wait – abandoned on shutdown).</summary>
    public static (long bytes, long items)? QueryRecycleBin(CancellationToken ct) =>
        StorageNative.QueryRecycleBinBounded(BinQueryTimeout, ct);

    private static CategoryResult MeasureRecycleBin(CategoryDef def, CancellationToken ct)
    {
        if (!AccountGuard.PersonalAllowed) return new CategoryResult { Def = def, Notes = { AccountGuard.Note } };
        if (QueryRecycleBin(ct) is not (long bytes, long items))
            return new CategoryResult { Def = def, Notes = { "Windows didn't report the Recycle Bin's size." } };
        return new CategoryResult { Def = def, Files = items, Bytes = bytes };
    }

    private static CategoryResult MeasureCategory(CategoryDef def, DateTime now, CancellationToken ct)
    {
        var roots = new List<RootStatus>();
        var top = new TopN<BigFile>(50);
        long files = 0, bytes = 0;
        bool busy = def.BlockProcesses.Length > 0 && ProcessCheck.AnyRunning(def.BlockProcesses, anySession: true);
        bool servicesUp = def.RequireStoppedServices.Length > 0 && !ProcessCheck.AllServicesStopped(def.RequireStoppedServices);
        var extra = new List<string>();

        foreach (var root in SafeRoots(def))
        {
            if (ct.IsCancellationRequested) break;
            if (root.PerUser && !AccountGuard.PersonalAllowed)
            {
                roots.Add(new RootStatus { Path = root.Path, State = RootState.OtherAccount });
                continue;
            }
            var rr = Resolve(root, out var state, out string? why);
            long f = 0, b = 0;
            var localTop = new TopN<BigFile>(50);
            if (rr != null)
            {
                var c = new WalkCtx(rr, root, now, ct);
                Walk(c, (path, e) => { f++; b += e.Length; localTop.Add(new BigFile(path, e.Length, e.WriteUtc), e.Length); }, null);
                if (c.SkippedDirs > 0) extra.Add($"{Ui.N(c.SkippedDirs)} folders in {root.Path} were left alone (links or not really inside).");
                state = RootState.Ready;
            }
            if (state is RootState.Ready or RootState.Denied)
            {
                if (root.NeedsAdmin && !Elevation.IsAdmin) state = RootState.NeedsAdmin;
                else if (root.Processes.Length > 0 && ProcessCheck.AnyRunning(root.Processes, anySession: false)) state = RootState.AppRunning;
                else if ((busy || servicesUp) && state == RootState.Ready) state = RootState.Busy;
            }
            if (state == RootState.Ready)
            {
                files += f; bytes += b;
                foreach (var x in localTop.ToSortedList()) top.Add(x, x.Size);
            }
            roots.Add(new RootStatus { Path = root.Path, State = state, Files = f, Bytes = b, Why = why, App = root.App });
        }

        // One redirected location is enough to distrust the whole category.
        bool redirected = roots.Any(r => r.State == RootState.Redirected);
        var notes = BuildNotes(def, roots, busy, servicesUp);
        notes.AddRange(extra.Take(3));
        if (redirected)
        {
            return new CategoryResult
            {
                Def = def,
                Roots = roots.Select(r => r.State == RootState.Ready
                    ? new RootStatus { Path = r.Path, State = RootState.Refused, Why = "another folder in this category looks redirected" } : r).ToList(),
                Notes = notes,
            };
        }
        return new CategoryResult { Def = def, Roots = roots, Files = files, Bytes = bytes, Top = top.ToSortedList(), Notes = notes };
    }

    private const string WuRunningNote =
        "Windows Update is running right now, which is normal – it starts on its own to check for updates. WinScope only " +
        "clears this cache while Windows Update and its download service (BITS) are stopped. Try again later, or after a " +
        "restart when no updates are waiting to install.";

    private static List<string> BuildNotes(CategoryDef def, List<RootStatus> roots, bool busy, bool servicesUp)
    {
        var notes = new List<string>();
        if (roots.Any(r => r.State == RootState.OtherAccount)) notes.Add(AccountGuard.Note);
        foreach (var g in roots.Where(r => r.State == RootState.AppRunning && r.App != null).GroupBy(r => r.App!))
        {
            long b = g.Sum(x => x.Bytes);
            notes.Add($"Close {g.Key} to clean this" + (b > 0 ? $" (≈{Format.Bytes(b)} waiting)." : "."));
        }
        var admin = roots.Where(r => r.State == RootState.NeedsAdmin).ToList();
        if (admin.Count > 0)
        {
            long b = admin.Sum(x => x.Bytes);
            string what = def.AdminOnly ? "Needs administrator rights" : "The system part needs administrator rights";
            notes.Add(what + (b > 0 ? $" (≈{Format.Bytes(b)} found)." : " to check and clean."));
        }
        if (servicesUp) notes.Add(WuRunningNote);
        else if (busy && def.BlockMessage != null) notes.Add(def.BlockMessage);
        foreach (var r in roots.Where(r => r.State == RootState.Redirected))
            notes.Add($"Skipped the whole category: {r.Path} – {r.Why ?? "its location looks redirected"}.");
        foreach (var r in roots.Where(r => r.State == RootState.Refused))
            notes.Add($"Not offered: {r.Path} – {r.Why}.");
        if (roots.Any(r => r.State == RootState.Denied)) notes.Add("Some folders couldn't be read.");
        return notes;
    }

    private static CategoryResult MeasureReport(CategoryDef def, CancellationToken ct)
    {
        if (def.PerUser && !AccountGuard.PersonalAllowed)
            return new CategoryResult { Def = def, Present = false, Notes = { AccountGuard.Note } };
        string? folder = def.Kind == CatKind.ReportDownloads ? StorageNative.DownloadsFolder() : Catalog.WindowsOldPath();
        if (folder == null || !Directory.Exists(folder))
            return new CategoryResult { Def = def, Folder = folder, Present = false };
        var (bytes, files, unreadable, done) = MeasureTree(folder, ct);
        var notes = new List<string>();
        if (unreadable > 0)
            notes.Add($"Incomplete: {Ui.N(unreadable)} folders couldn't be read" + (Elevation.IsAdmin ? "" : " (they need administrator rights)") +
                      ", so the real size is larger.");
        if (!done) notes.Add("Measuring was interrupted, so this is a partial figure.");
        return new CategoryResult { Def = def, Folder = folder, Files = files, Bytes = bytes, Notes = notes };
    }

    /// <summary>Total size of a folder tree (no links followed, online-only files not counted).</summary>
    private static (long bytes, long files, long unreadable, bool done) MeasureTree(string folder, CancellationToken ct)
    {
        long bytes = 0, files = 0, unreadable = 0;
        var stack = new Stack<string>();
        stack.Push(Path.GetFullPath(folder));
        var buf = new List<DirEntry>(256);
        while (stack.Count > 0)
        {
            if (ct.IsCancellationRequested) return (bytes, files, unreadable, false);
            string dir = stack.Pop();
            var st = FsWalk.List(dir, buf, long.MaxValue, ct);
            if (st == ListStatus.Cancelled) return (bytes, files, unreadable, false);
            if (st is ListStatus.Denied or ListStatus.Failed || (buf.Count == 0 && StorageNative.IsUnreadable(dir))) unreadable++;
            foreach (var e in buf)
            {
                if (FileRules.IsLink(e.Attributes)) continue;
                if (e.IsDirectory) { if (e.Name != null) stack.Push(FsWalk.Join(dir, e.Name)); continue; }
                if (FileRules.IsCloudOnly(e.Attributes)) continue;
                files++; bytes += e.Length;
            }
        }
        return (bytes, files, unreadable, true);
    }

    private static IEnumerable<CleanRoot> SafeRoots(CategoryDef def)
    {
        try { return def.Roots(); }
        catch (Exception ex) { App.LogError(ex); return Array.Empty<CleanRoot>(); }
    }

    // ================================================================== roots

    private static string TrimSep(string p) => p.Length > 3 ? p.TrimEnd('\\') : p;

    /// <summary>
    /// Check a root on disk. Returns null (with a state and reason) unless it's a real, readable folder that is not
    /// itself a link, passes the root sanity rules both as given and where it really lives, and really sits inside its
    /// expected parent with no link anywhere in between.
    /// </summary>
    internal static ResolvedRoot? Resolve(CleanRoot r, out RootState state, out string? why)
    {
        state = RootState.Refused;
        if (r.TempRoot)
        {
            why = SafetyRules.WhyBadTempValue(r.Path) ?? (Path.IsPathFullyQualified(r.Path) ? null : "it isn't a full path");
            if (why != null) return null;
        }
        string raw, parentRaw;
        // GetFullPath doesn't expand 8.3 short names (C:\Users\GEORGE~1\…), so expand them before comparing.
        try
        {
            raw = TrimSep(StorageNative.ExpandShortNames(Path.GetFullPath(r.Path)));
            parentRaw = TrimSep(StorageNative.ExpandShortNames(Path.GetFullPath(r.ExpectedParent)));
        }
        catch { why = "its path isn't valid"; return null; }
        string? norm = WinPath.Normalize(raw), parentNorm = WinPath.Normalize(parentRaw);
        // GetFullPath trims trailing dots/spaces, so check the value as given too.
        if (WinPath.HasTrailingDotOrSpaceSegment(r.Path) || WinPath.HasTrailingDotOrSpaceSegment(raw)) { why = "its name is unusual"; return null; }
        why = SafetyRules.WhyBadCleanupRoot(norm, r.Recurse, r.Pattern, SafetyContext.Critical);
        if (why != null) return null;

        using var h = StorageNative.Open(raw, forDelete: false, listDirectory: true, out int err);
        if (h.IsInvalid)
        {
            if (StorageNative.IsGone(err)) { state = RootState.Missing; why = null; }
            else if (err == StorageNative.ERROR_ACCESS_DENIED) { state = RootState.Denied; why = "access denied"; }
            else { state = RootState.Denied; why = $"couldn't open it (error {err})"; }
            return null;
        }
        if (!StorageNative.TryGetFacts(h, out var f)) { why = "couldn't read its details"; return null; }
        if (!f.IsDirectory) { why = "it isn't a folder"; return null; }
        if (FileRules.IsLink(f.Attributes)) { state = RootState.Redirected; why = "it's a link to somewhere else"; return null; }
        string? finalRaw = StorageNative.FinalPathRaw(h);
        if (finalRaw == null || WinPath.HasTrailingDotOrSpaceSegment(finalRaw)) { why = "Windows couldn't say where it really is"; return null; }
        string? final = WinPath.Normalize(finalRaw);
        why = SafetyRules.WhyBadCleanupRoot(final, r.Recurse, r.Pattern, SafetyContext.Critical);
        if (why != null) return null;
        if (StorageNative.TryGetFileId(h) is not FileId id) { why = "Windows couldn't identify the folder"; return null; }

        // Placement on real locations: temp root = the real %LOCALAPPDATA%\Temp; root really inside its parent at the
        // same relative path as given (so no link in between) …
        string? parentFinal = SafetyContext.FinalOf(parentRaw);
        var (redirected, pwhy) = SafetyRules.CheckPlacement(norm, parentNorm, final, parentFinal, r.TempRoot,
            SafetyContext.LocalTemp.final);
        if (pwhy != null) { state = redirected ? RootState.Redirected : RootState.Refused; why = pwhy; return null; }
        // … and every folder between parent and root, as given, is a real folder rather than a link.
        for (int d = WinPath.Depth(parentNorm!) + 1; d < WinPath.Depth(norm!); d++)
        {
            string mid = WinPath.PrefixAtDepth(norm!, d)!;
            using var mh = StorageNative.Open(mid, forDelete: false, listDirectory: false, out _);
            if (mh.IsInvalid || !StorageNative.TryGetFacts(mh, out var mf) || !mf.IsDirectory || FileRules.IsLink(mf.Attributes))
            { state = RootState.Redirected; why = $"{mid} is a link or couldn't be checked"; return null; }
        }

        state = RootState.Ready;
        return new ResolvedRoot(raw, norm!, final!, id);
    }

    // ================================================================== walking

    /// <summary>State for one walk of one root (also the per-root tally for the log).</summary>
    private sealed class WalkCtx
    {
        public readonly ResolvedRoot Rr;
        public readonly CleanRoot Root;
        public readonly DateTime Now;
        public readonly CancellationToken Ct;
        public bool Stop;
        public long Links, Unusual, SkippedDirs;
        public long Files, Bytes, Pending, PendingBytes, Dirs;
        public readonly SkipCounts Skips = new();
        private string? _verifiedPrefix;

        public WalkCtx(ResolvedRoot rr, CleanRoot root, DateTime now, CancellationToken ct) { Rr = rr; Root = root; Now = now; Ct = ct; }

        public bool Stopped => Stop || Ct.IsCancellationRequested;

        /// <summary>
        /// Identity check: the part of <paramref name="finalNorm"/> at the root's depth must be the very same folder
        /// as the root (FILE_ID_INFO), not just a name that compares equal case-insensitively.
        /// </summary>
        public bool UnderRootById(string? finalNorm)
        {
            if (finalNorm == null || !WinPath.IsInside(finalNorm, Rr.Final)) return false;
            string? prefix = WinPath.PrefixAtDepth(finalNorm, WinPath.Depth(Rr.Final));
            if (prefix == null) return false;
            if (string.Equals(prefix, _verifiedPrefix, StringComparison.Ordinal)) return true;
            if (StorageNative.FileIdOf(prefix) is FileId id && id == Rr.Id) { _verifiedPrefix = prefix; return true; }
            return false;
        }
    }

    /// <summary>
    /// Walk a resolved root with an explicit stack. Each sub-folder is re-checked through a handle before it's
    /// listed (still a real folder, really inside the root by path and id, and for age-limited categories neither
    /// it nor a folder above it changed recently). <paramref name="onFile"/> gets every file passing the filter as
    /// enumerated; <paramref name="onDir"/> every folder entered, with its details from BEFORE anything inside
    /// was deleted (parents come before children).
    /// </summary>
    private static void Walk(WalkCtx c, Action<string, DirEntry> onFile, Action<string, FileFacts>? onDir)
    {
        var stack = new Stack<(string dir, bool old, bool isRoot)>();
        stack.Push((c.Rr.Raw, true, true));
        var buf = new List<DirEntry>(256);
        while (stack.Count > 0 && !c.Stopped)
        {
            var (dir, parentsOld, isRoot) = stack.Pop();
            bool old = parentsOld;
            if (!isRoot)
            {
                using (var h = StorageNative.Open(dir, forDelete: false, listDirectory: false, out _))
                {
                    if (h.IsInvalid || !StorageNative.TryGetFacts(h, out var df)) { c.SkippedDirs++; continue; }
                    string? fr = StorageNative.FinalPathRaw(h);
                    var (safe, isOld) = DeleteGate.DirEnterable(df, fr, c.Rr.Final, c.Root.MinAge, c.Now, parentsOld);
                    if (!safe || !c.UnderRootById(fr == null ? null : WinPath.Normalize(fr))) { c.SkippedDirs++; continue; }
                    if (c.Root.MinAge != null && !isOld) continue;            // recent folder: everything below gets the grace period
                    old = isOld;
                    onDir?.Invoke(dir, df);
                }
            }
            var st = FsWalk.List(dir, buf, 0, c.Ct);
            if (st == ListStatus.Cancelled) break;
            foreach (var e in buf)
            {
                if (c.Stopped) break;
                if (e.Name == null) continue;
                if (FileRules.HasOddEnd(e.Name)) { c.Unusual++; continue; }        // Win32 would act on a trimmed name
                if (e.IsDirectory)
                {
                    if (FileRules.IsLink(e.Attributes)) { c.Links++; continue; }    // never followed, never deleted
                    if (c.Root.Recurse) stack.Push((FsWalk.Join(dir, e.Name), old, false));
                    continue;
                }
                if (FileRules.IsLink(e.Attributes)) { c.Links++; continue; }
                if (!FileRules.Qualifies(e.Name, e.Attributes, e.CreationUtc, e.WriteUtc, c.Now, c.Root.Pattern, c.Root.MinAge)) continue;
                onFile(FsWalk.Join(dir, e.Name), e);
            }
        }
    }

    // ================================================================== clean

    /// <summary>
    /// Clean the confirmed categories, touching only the roots that were confirmed. Re-checks every root and file.
    /// Writes a log line after each root. Never throws; cancellation stops after the current file.
    /// </summary>
    public async Task<List<CleanOutcome>> CleanAsync(IReadOnlyList<CleanRequest> requests, IntPtr hwnd,
        Action<CleanProgress> progress, CancellationToken ct)
    {
        var results = new List<CleanOutcome>();
        bool entered = false;
        try
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            entered = true;
            lock (_lock) _cleaning = true;
            Publish();
            foreach (var req in requests)
            {
                if (ct.IsCancellationRequested) break;
                var o = new CleanOutcome { Def = req.Def };
                results.Add(o);
                var p = new CleanProgress { Category = req.Def.Name };
                progress(p);
                try
                {
                    if (req.Def.ReportOnly) continue;
                    if (req.Def.Kind == CatKind.RecycleBin) await EmptyRecycleBin(req, o, hwnd, ct).ConfigureAwait(false);
                    else await Task.Run(() => CleanCategory(req, o, p, progress, ct), CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    App.LogError(ex);
                    o.Notes.Add("Stopped early: " + ex.Message);
                    WriteLog(req.Def.Name, null, o.Files, o.Bytes, o.Skipped, "stopped by an error");
                }
                if (ct.IsCancellationRequested) o.Cancelled = true;
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            if (entered) _gate.Release();
            lock (_lock) _cleaning = false;
            Publish();
        }
        return results;
    }

    private async Task EmptyRecycleBin(CleanRequest req, CleanOutcome o, IntPtr hwnd, CancellationToken ct)
    {
        if (!AccountGuard.PersonalAllowed) { o.Notes.Add(AccountGuard.Note); return; }
        // Re-query right before emptying: if anything was added since the preview, don't empty it unseen.
        var before = QueryRecycleBin(ct);
        if (before is not (long b0, long i0)) { o.Notes.Add("Couldn't check the Recycle Bin, so it wasn't emptied."); Log(); return; }
        if (i0 > req.PreviewFiles || b0 > req.PreviewBytes)
        {
            o.Notes.Add("Not emptied: something was added to the Recycle Bin since you confirmed. Check it and try again.");
            Log();
            return;
        }
        if (ct.IsCancellationRequested) return;
        int hr = await StorageNative.RunSta(() => StorageNative.EmptyRecycleBin(hwnd)).ConfigureAwait(false);
        var after = QueryRecycleBin(CancellationToken.None);
        if (after is (long b1, long i1))
        {
            o.Bytes = Math.Max(0, b0 - b1);
            o.Files = Math.Max(0, i0 - i1);
            if (i1 > 0) o.Notes.Add($"{Ui.N(i1)} items were left in the Recycle Bin.");
        }
        if (hr != 0 && hr != StorageNative.E_UNEXPECTED) o.Notes.Add($"Windows reported a problem emptying it (0x{hr:X8}).");
        Log();

        void Log() => WriteLog(req.Def.Name, null, o.Files, o.Bytes, o.Skipped, string.Join(" ", o.Notes));
    }

    private void CleanCategory(CleanRequest req, CleanOutcome o, CleanProgress p, Action<CleanProgress> progress, CancellationToken ct)
    {
        var def = req.Def;
        if (def.BlockProcesses.Length > 0 && ProcessCheck.AnyRunning(def.BlockProcesses, anySession: true))
        {
            o.Notes.Add(def.BlockMessage ?? "Skipped – something is using these files.");
            WriteLog(def.Name, null, 0, 0, o.Skipped, "skipped: busy");
            return;
        }
        if (def.RequireStoppedServices.Length > 0 && !ProcessCheck.AllServicesStopped(def.RequireStoppedServices))
        {
            o.Notes.Add(WuRunningNote);
            WriteLog(def.Name, null, 0, 0, o.Skipped, "skipped: Windows Update is running");
            return;
        }
        var now = DateTime.UtcNow;
        var confirmed = new HashSet<string>(req.RootPaths, StringComparer.OrdinalIgnoreCase);

        // Resolve every confirmed root first: one redirected location stops the whole category.
        var ready = new List<(CleanRoot root, ResolvedRoot rr)>();
        foreach (var root in SafeRoots(def))
        {
            if (!confirmed.Contains(root.Path)) continue;                 // new since the preview → not confirmed
            if (root.PerUser && !AccountGuard.PersonalAllowed) { o.Notes.Add(AccountGuard.Note); continue; }
            if (root.NeedsAdmin && !Elevation.IsAdmin) { o.Notes.Add($"Skipped {root.Path} – needs administrator rights."); continue; }
            if (root.Processes.Length > 0 && ProcessCheck.AnyRunning(root.Processes, anySession: false))
            {
                o.Notes.Add($"Skipped {root.App ?? "an app"}'s cache – close {root.App ?? "it"} to clean it.");
                continue;
            }
            var rr = Resolve(root, out var state, out string? why);
            if (state == RootState.Redirected)
            {
                o.Notes.Add($"Stopped: {root.Path} – {why ?? "its location looks redirected"}, so nothing in this category was cleaned.");
                WriteLog(def.Name, root.Path, 0, 0, o.Skipped, "skipped: location looks redirected");
                return;
            }
            if (rr == null)
            {
                if (state != RootState.Missing) o.Notes.Add($"Left alone: {root.Path} – {why ?? "couldn't check it"}.");
                continue;
            }
            ready.Add((root, rr));
        }

        foreach (var (root, rr) in ready)
        {
            if (ct.IsCancellationRequested || o.StoppedForGrowth) break;
            var c = new WalkCtx(rr, root, now, ct);
            CleanRootFiles(c, req, o, p, progress);
            // Merge this root's tally and log it straight away.
            o.Files += c.Files; o.Bytes += c.Bytes; o.Dirs += c.Dirs; o.PendingFiles += c.Pending; o.PendingBytes += c.PendingBytes;
            if (c.Links > 0) c.Skips.Add(SkipReason.Linked, c.Links);
            if (c.Unusual > 0) c.Skips.Add(SkipReason.Unusual, c.Unusual);
            if (c.SkippedDirs > 0) c.Skips.Add(SkipReason.Changed, c.SkippedDirs);
            o.Skipped.AddAll(c.Skips);
            var bits = new List<string>();
            if (c.Pending > 0) bits.Add($"{c.Pending} files ({c.PendingBytes} bytes) freed when the app using them closes");
            if (o.StoppedForGrowth) bits.Add("stopped: much more than the preview");
            if (ct.IsCancellationRequested) bits.Add("cancelled");
            WriteLog(def.Name, root.Path, c.Files, c.Bytes, c.Skips, string.Join("; ", bits));
        }
        if (o.StoppedForGrowth)
            o.Notes.Add($"Stopped part-way: there was much more here than the preview showed ({Ui.Files(req.PreviewFiles)}, " +
                        $"{Format.Bytes(req.PreviewBytes)}). Check again and look at the preview before cleaning the rest.");
        p.Files = o.Files; p.Bytes = o.Bytes; p.Skipped = o.Skipped.Total;
        progress(p);
    }

    private static void CleanRootFiles(WalkCtx c, CleanRequest req, CleanOutcome o, CleanProgress p, Action<CleanProgress> progress)
    {
        var dirs = new List<(string path, FileFacts facts)>();
        long tick = 0;
        Walk(c,
            (path, e) =>
            {
                DeleteOne(path, c, req, o);
                if ((++tick & 63) == 0)
                {
                    p.Files = o.Files + c.Files; p.Bytes = o.Bytes + c.Bytes; p.Skipped = o.Skipped.Total + c.Skips.Total;
                    progress(p);
                }
            },
            (path, f) => dirs.Add((path, f)));

        // Emptied sub-folders, deepest first (reverse discovery order). The root itself is never in 'dirs'.
        if (c.Root.Recurse && !c.Stopped)
            for (int i = dirs.Count - 1; i >= 0 && !c.Stopped; i--)
            {
                var (path, f) = dirs[i];
                // Times seen BEFORE we deleted anything inside (deleting updates the folder's modified/change time).
                if (FileRules.IsOldEnough(f.CreationUtc, f.WriteUtc, f.ChangeUtc, c.Now, c.Root.MinAge)) RemoveIfEmpty(path, c);
            }
    }

    /// <summary>The per-file gate. Every check is repeated on the opened handle, so what we delete is what we checked.</summary>
    private static void DeleteOne(string path, WalkCtx c, CleanRequest req, CleanOutcome o)
    {
        var rr = c.Rr;
        // 1. Lexical: the path as built must be strictly inside the root as given.
        if (!DeleteGate.PathAllowed(path, rr.Norm, c.Root.Recurse)) { c.Skips.Add(SkipReason.Changed); return; }

        // 2. Open the entry itself (never a link target) with DELETE access.
        var h = StorageNative.Open(path, forDelete: true, listDirectory: false, out int err);
        long size;
        try
        {
            if (h.IsInvalid)
            {
                if (!StorageNative.IsGone(err)) c.Skips.Add(StorageNative.Classify(err));
                return;
            }
            if (!StorageNative.TryGetFacts(h, out var f)) { c.Skips.Add(SkipReason.Other); return; }

            // 3. What the handle says: a plain, on-disk, single-link file that matches the filter (incl. change time)
            //    and really lives strictly inside where the root really is – by path and by file id.
            string? finalRaw = StorageNative.FinalPathRaw(h);
            if (DeleteGate.Check(f, finalRaw, rr.Final, c.Root.Recurse, c.Root.Pattern, c.Root.MinAge, c.Now) is SkipReason why)
            { c.Skips.Add(why); return; }
            if (!c.UnderRootById(WinPath.Normalize(finalRaw!))) { c.Skips.Add(SkipReason.Changed); return; }

            // 4. Much bigger than the preview? Stop this category and say so.
            if (GrowthLimit.WouldExceed(req.PreviewFiles, req.PreviewBytes,
                    o.Files + o.PendingFiles + c.Files + c.Pending, o.Bytes + o.PendingBytes + c.Bytes + c.PendingBytes, f.Size))
            {
                o.StoppedForGrowth = true;
                c.Stop = true;
                return;
            }

            // 5. Delete (takes effect when our handle closes).
            if (!StorageNative.MarkForDelete(h, out err)) { c.Skips.Add(StorageNative.Classify(err)); return; }
            size = f.Size;
        }
        finally { h.Dispose(); }

        // 6. Gone now, or still open in another app (freed when it closes the file)?
        if (StorageNative.IsReallyGone(path)) { c.Files++; c.Bytes += size; }
        else { c.Pending++; c.PendingBytes += size; }
    }

    private static void RemoveIfEmpty(string path, WalkCtx c)
    {
        if (!DeleteGate.PathAllowed(path, c.Rr.Norm, recurse: true)) return;
        using var h = StorageNative.Open(path, forDelete: true, listDirectory: false, out _);
        if (h.IsInvalid || !StorageNative.TryGetFacts(h, out var f)) return;
        string? fr = StorageNative.FinalPathRaw(h);
        if (!DeleteGate.DirRemovable(f, fr, c.Rr.Final, c.Root.MinAge, c.Now)) return;      // never the root itself
        if (!c.UnderRootById(WinPath.Normalize(fr!))) return;
        // Fails with ERROR_DIR_NOT_EMPTY unless it really is empty – that's the point.
        if (StorageNative.MarkForDelete(h, out _)) c.Dirs++;
    }

    /// <summary>One log line per root (or per category for the Recycle Bin). Kept in memory; written via UserFiles when allowed.</summary>
    private void WriteLog(string category, string? root, long files, long bytes, SkipCounts skipped, string? note)
    {
        try
        {
            string cat = root == null ? category : $"{category} – {root}";
            string line = CleanupLog.FormatLine(DateTime.Now, cat, files, bytes, skipped, string.IsNullOrWhiteSpace(note) ? null : note);
            lock (_lock) { _sessionLog.Add(line); if (_sessionLog.Count > 2000) _sessionLog.RemoveAt(0); }
            UserFiles.TryAppend(LogName, line + Environment.NewLine);       // refuses (by design) while elevated
        }
        catch (Exception ex) { App.LogError(ex); }
    }
}
