using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// One server's open span baselines, <c>{partition}/{session}/{tree key}.json</c>. Closing a span writes
/// its event to the <see cref="WorkspaceSpool"/> and then deletes the baseline, under the session's lock;
/// a baseline whose span is already spooled was closed by a hook that died before the delete, and is
/// deleted without closing again. Nothing here touches the network.
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
    public async Task<bool> OpenAsync(string sessionId, string dir, WorkspaceOpen mode, TimeSpan budget) {
        if (SessionDir(sessionId) is not { } sessionDir) return false;

        var started = time.GetTimestamp();
        TimeSpan Left() => budget - time.GetElapsedTime(started);

        using var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait);
        if (held is null) return false;

        if (mode == WorkspaceOpen.Turn) await CloseAllLockedAsync(sessionId, sessionDir, Left);

        var repo = await snapshotter.ResolveAsync(dir, Left());
        if (repo is null) return false;

        var path = Path.Combine(sessionDir, $"{repo.Key}.json");
        // A baseline still here after a Turn close is one whose close could not read the tree; the next
        // close then spans both turns rather than losing the first.
        if (File.Exists(path)) return true;

        var snapshot = await snapshotter.TakeAsync(repo.Toplevel, Min(snapshotter.Bounds.SnapshotBudget, Left()));
        if (snapshot is null) return false;

        var span     = new WorkspaceSpan(Guid.NewGuid(), sessionId, repo.Toplevel, time.GetUtcNow(), snapshot);

        return WorkspaceFiles.WriteAtomic(path, JsonSerializer.Serialize(span, WorkspaceJsonContext.Default.WorkspaceSpan));
    }

    /// <summary>Closes every open span of the session, spooling the changes of each.</summary>
    public async Task<bool> CloseAllAsync(string sessionId, TimeSpan budget) {
        if (SessionDir(sessionId) is not { } sessionDir || !Directory.Exists(sessionDir)) return true;

        var started = time.GetTimestamp();
        using var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait);
        if (held is null) return false;

        await CloseAllLockedAsync(sessionId, sessionDir, () => budget - time.GetElapsedTime(started));
        return true;
    }

    /// <summary>Closes every span and then deletes the session's baselines; its spooled events are the spool's.</summary>
    public async Task EndSessionAsync(string sessionId, TimeSpan budget) {
        if (SessionDir(sessionId) is not { } sessionDir || !Directory.Exists(sessionDir)) return;

        var started = time.GetTimestamp();
        using (var held = WorkspaceSpanLock.TryAcquire(sessionDir, LockWait)) {
            if (held is null) return;

            await CloseAllLockedAsync(sessionId, sessionDir, () => budget - time.GetElapsedTime(started));
            foreach (var baseline in Baselines(sessionDir)) WorkspaceFiles.TryDelete(baseline);
        }

        TryDeleteDirectory(sessionDir);
    }

    /// <summary>Drops the session's baselines unposted — a session whose recording was turned off.</summary>
    public void Discard(string sessionId) {
        if (SessionDir(sessionId) is { } sessionDir && Directory.Exists(sessionDir)) TryDeleteDirectory(sessionDir);
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
                    var info   = new DirectoryInfo(session);
                    var latest = info.EnumerateFiles().Select(f => f.LastWriteTimeUtc).Append(info.LastWriteTimeUtc).Max();
                    if (latest < cutoff) TryDeleteDirectory(session);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // The next prune retries it.
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The next prune retries.
        }
    }

    static IEnumerable<string> Baselines(string sessionDir) {
        try {
            return Directory.EnumerateFiles(sessionDir, "*.json").ToList();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return [];
        }
    }

    async Task CloseAllLockedAsync(string sessionId, string sessionDir, Func<TimeSpan> left) {
        foreach (var path in Baselines(sessionDir)) {
            WorkspaceSpan? span;
            try {
                span = JsonSerializer.Deserialize(await File.ReadAllTextAsync(path), WorkspaceJsonContext.Default.WorkspaceSpan);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
                span = null;
            }

            if (span is null || spool.Has(sessionId, span.SpanId)
             || time.GetUtcNow() - span.OpenedAt > MaxSpanAge || !Directory.Exists(span.RepoRoot)) {
                WorkspaceFiles.TryDelete(path);
                continue;
            }

            var snapshotBudget = Min(snapshotter.Bounds.SnapshotBudget, left());
            var after          = await snapshotter.TakeAsync(span.RepoRoot, snapshotBudget);
            // Kept for the next close, which then spans both turns.
            if (after is null) continue;

            var detection      = await _detector.DetectAsync(span.RepoRoot, span.Snapshot, after, Max(left(), TimeSpan.FromMilliseconds(250)));

            if (detection.Changes.Count > 0 && !spool.Write(Fit(span, detection))) continue;

            WorkspaceFiles.TryDelete(path);
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

    static void TryDeleteDirectory(string dir) {
        try { Directory.Delete(dir, recursive: true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
