using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// One server's open span baselines, <c>{partition}/{session}/{tree key}.json</c>. Git runs outside the
/// session's lock, which a waiting hook gives up on after a second; under it, closing a span writes its
/// event to the <see cref="WorkspaceSpool"/> and then deletes the baseline. A baseline whose span is already
/// spooled was closed by a hook that died before the delete, and is deleted without closing again. Nothing
/// here touches the network.
/// </summary>
public sealed class WorkspaceSpanStore(
        SpoolPartition partition, WorkspaceSpool spool, WorkspaceSnapshotter snapshotter, TimeProvider time) {
    public const string DirectoryName = "workspace-spans";

    public static readonly TimeSpan MaxSpanAge = TimeSpan.FromHours(24);
    public static readonly TimeSpan PruneAfter = TimeSpan.FromDays(7);

    readonly WorkspaceChangeDetector _detector = new(snapshotter.Git, time, snapshotter.Bounds);

    public WorkspaceSpanStore(ConfigRoot config, string? server, WorkspaceSpool spool, WorkspaceSnapshotter snapshotter, TimeProvider time)
        : this(SpoolPartition.Server(config.Path(DirectoryName), server), spool, snapshotter, time) { }

    public TimeSpan LockWait { get; init; } = WorkspaceSpanLock.DefaultWait;

    public string DirectoryPath => partition.DirectoryPath;

    string? SessionDir(string sessionId) => HookSpool.KeyFor(sessionId) is { } key ? Path.Combine(partition.DirectoryPath, key) : null;

    /// <summary>Opens a span over the tree containing <paramref name="dir"/>. False when nothing was opened:
    /// outside a repository, an unusable session id, or the lock held past its wait.</summary>
    /// <param name="run">The run opening the span, which an end bounded to its own run can tell from a later one.</param>
    public async Task<bool> OpenAsync(string sessionId, string dir, WorkspaceOpen mode, TimeSpan budget, WorkspaceRun? run = null) {
        if (SessionDir(sessionId) is not { } sessionDir) return false;

        var started = time.GetTimestamp();
        TimeSpan Left() => budget - time.GetElapsedTime(started);

        if (mode is WorkspaceOpen.Turn or WorkspaceOpen.Run && !await CloseAllCoreAsync(sessionId, sessionDir, Left)) return false;

        var repo = await snapshotter.ResolveAsync(dir, Left());
        if (repo is null) return false;

        var path   = Path.Join(sessionDir, $"{repo.Key}.json");
        var reuses = mode != WorkspaceOpen.Run;
        if (reuses && File.Exists(path)) return true;

        var snapshot = await snapshotter.TakeAsync(repo.Toplevel, Min(snapshotter.Bounds.SnapshotBudget, Left()));
        if (snapshot is null) return false;

        using var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait);
        if (held is null) return false;

        // A baseline here now is a concurrent open of the same tree, or one whose close could not read the
        // tree; either way the earlier baseline is kept, so the next close spans both turns.
        if (reuses && File.Exists(path)) return true;

        var span = new WorkspaceSpan(Guid.NewGuid(), sessionId, repo.Toplevel, time.GetUtcNow(), snapshot, run);
        return WorkspaceFiles.WriteAtomic(path, JsonSerializer.Serialize(span, WorkspaceJsonContext.Default.WorkspaceSpan));
    }

    /// <summary>Closes every open span of the session, spooling the changes of each.</summary>
    public async Task<bool> CloseAllAsync(string sessionId, TimeSpan budget) {
        if (SessionDir(sessionId) is not { } sessionDir || !Directory.Exists(sessionDir)) return true;

        var started = time.GetTimestamp();
        return await CloseAllCoreAsync(sessionId, sessionDir, () => budget - time.GetElapsedTime(started));
    }

    /// <summary>Closes every span, then removes the session's directory once no baseline is left. A span
    /// whose close failed keeps its baseline for a later hook to retry; spooled events are the spool's.</summary>
    /// <param name="run">Closes only that run's spans, so the end of one run cannot close the spans of a run that started
    /// after it.</param>
    public async Task EndSessionAsync(string sessionId, TimeSpan budget, WorkspaceRunCutoff? run = null) {
        if (SessionDir(sessionId) is not { } sessionDir || !Directory.Exists(sessionDir)) return;

        var started = time.GetTimestamp();
        await CloseAllCoreAsync(sessionId, sessionDir, () => budget - time.GetElapsedTime(started), run);

        using var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait);
        if (held is not null && !Baselines(sessionDir).Any()) held.RemoveSession();
    }

    /// <summary>Whether anything of the session's spans is left.</summary>
    public bool Holds(string sessionId) => SessionDir(sessionId) is { } sessionDir && Directory.Exists(sessionDir);

    /// <summary>Whether a span of <paramref name="run"/> is left.</summary>
    public bool Holds(string sessionId, WorkspaceRunCutoff run) {
        if (SessionDir(sessionId) is not { } sessionDir || !Directory.Exists(sessionDir)) return false;

        // A directory that cannot be listed may still hold the run's spans, so the close owed for them is kept.
        try {
            return Directory.EnumerateFiles(sessionDir, "*.json").Any(path => ReadSpan(path) is not { } span || run.Covers(span));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return true;
        }
    }

    /// <summary>Drops the session's baselines unposted — a session whose recording was turned off.</summary>
    /// <returns>Whether no baseline is left: false when the lock was held past its wait, or a baseline
    /// could not be deleted.</returns>
    public bool Discard(string sessionId) {
        if (SessionDir(sessionId) is not { } sessionDir) return false;
        if (!Directory.Exists(sessionDir)) return true;

        using var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait);
        if (held is null) return false;

        held.RemoveSession();
        return !Baselines(sessionDir).Any();
    }

    /// <summary>Deletes session directories nothing has written for <see cref="PruneAfter"/>, under every server.</summary>
    public void Prune() {
        var top = Path.GetDirectoryName(partition.DirectoryPath)!;
        if (!Directory.Exists(top)) return;

        var cutoff = (time.GetUtcNow() - PruneAfter).UtcDateTime;
        try {
            foreach (var server in Directory.EnumerateDirectories(top))
            foreach (var session in Directory.EnumerateDirectories(server)) {
                try {
                    if (!IsStale(session, cutoff, withDirectory: true)) continue;

                    // Never waits: this runs on every hook, and a busy directory is left for a later prune.
                    using var held = WorkspaceSpanLock.TryAcquire(session, TimeSpan.Zero);
                    // Again under the lock: a hook may have opened a span between the first look and the lock.
                    // Taking the lock touches the directory and may create the lock file, so only the other
                    // files count now.
                    if (held is not null && IsStale(session, cutoff, withDirectory: false)) held.RemoveSession();
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // The next prune retries it.
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The next prune retries.
        }
    }

    static bool IsStale(string session, DateTime cutoff, bool withDirectory) {
        var info  = new DirectoryInfo(session);
        var times = info.EnumerateFiles().Where(f => withDirectory || f.Name != ".lock").Select(f => f.LastWriteTimeUtc);
        if (withDirectory) times = times.Append(info.LastWriteTimeUtc);
        return times.DefaultIfEmpty(DateTime.MinValue).Max() < cutoff;
    }

    static IEnumerable<string> Baselines(string sessionDir) {
        try {
            return Directory.EnumerateFiles(sessionDir, "*.json").ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    /// <summary>What closing one baseline comes to, worked out without the lock: drop it, spool an event
    /// and drop it, or keep it for a later close.</summary>
    sealed record Closing(string Path, Guid? SpanId, WorkspaceChangesRequest? Event, bool Keep);

    /// <summary>Snapshots and diffs every open span outside the lock, then commits each under it — only if
    /// its baseline is still the one diffed, so a span a concurrent hook closed meanwhile is never spooled
    /// twice. False when the lock was held past its wait.</summary>
    async Task<bool> CloseAllCoreAsync(string sessionId, string sessionDir, Func<TimeSpan> left, WorkspaceRunCutoff? run = null) {
        var closings = new List<Closing>();
        foreach (var path in Baselines(sessionDir).Where(path => run is null || ReadSpan(path) is not { } span || run.Covers(span)))
            closings.Add(await PrepareAsync(sessionId, path, left));
        if (closings.Count == 0) return true;

        using var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait);
        if (held is null) return false;

        foreach (var closing in closings) {
            if (closing.Keep || ReadSpan(closing.Path)?.SpanId != closing.SpanId) continue;
            if (closing.Event is { } evt && !spool.Has(sessionId, evt.SpanId) && !spool.Write(evt)) continue;

            WorkspaceFiles.TryDelete(closing.Path);
        }

        return true;
    }

    async Task<Closing> PrepareAsync(string sessionId, string path, Func<TimeSpan> left) {
        var span = ReadSpan(path);
        if (span is null || spool.Has(sessionId, span.SpanId)
         || time.GetUtcNow() - span.OpenedAt > MaxSpanAge || !Directory.Exists(span.RepoRoot))
            return new Closing(path, span?.SpanId, null, Keep: false);

        var after = await snapshotter.TakeAsync(span.RepoRoot, Min(snapshotter.Bounds.SnapshotBudget, left()));
        if (after is null) return new Closing(path, span.SpanId, null, Keep: true);

        var detection = await _detector.DetectAsync(span.RepoRoot, span.Snapshot, after, Max(left(), TimeSpan.FromMilliseconds(250)));
        return new Closing(path, span.SpanId, detection.Changes.Count > 0 ? Fit(span, detection) : null, Keep: false);
    }

    static WorkspaceSpan? ReadSpan(string path) {
        try {
            return JsonSerializer.Deserialize(File.ReadAllText(path), WorkspaceJsonContext.Default.WorkspaceSpan);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            return null;
        }
    }

    /// <summary>One span is one event of at most <see cref="WorkspaceBounds.EventBytes"/> serialized; past
    /// that its changes are cut off and it is marked truncated.</summary>
    WorkspaceChangesRequest Fit(WorkspaceSpan span, WorkspaceDetection detection) {
        var request = new WorkspaceChangesRequest(
            span.SessionId, span.SpanId, span.RepoRoot, span.OpenedAt, time.GetUtcNow(), detection.Changes, detection.Truncated);

        var limit = snapshotter.Bounds.EventBytes;
        var size  = Size(request);
        while (size > limit && request.Changes.Count > 0) {
            var keep = (int) (request.Changes.Count * ((double) limit / size) * 0.95);
            request = request with { Changes = request.Changes.Take(Math.Min(keep, request.Changes.Count - 1)).ToList(), Truncated = true };
            size    = Size(request);
        }

        return request;
    }

    static int Size(WorkspaceChangesRequest request) =>
        Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request, WorkspaceJsonContext.Default.WorkspaceChangesRequest));

    static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    static TimeSpan Max(TimeSpan a, TimeSpan b) => a > b ? a : b;
}
