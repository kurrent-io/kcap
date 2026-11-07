using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// Closed spans waiting to be posted, one file per span: <c>{partition}/{session}/{SpanId}.json</c>. A
/// store of its own, so a workspace event can never evict a lifecycle one. The server derives the event
/// id from the span id, so a span delivered twice is stored once.
/// </summary>
public sealed class WorkspaceSpool(SpoolPartition partition, TimeProvider time, Action<string>? warn = null, long capBytes = WorkspaceSpool.DefaultCapBytes) {
    public const string DirectoryName  = "workspace-spool";
    public const long   DefaultCapBytes = 8L * 1024 * 1024;
    public const string RoutePath      = "/hooks/workspace-changes";

    /// <summary>How long a server whose probe says it lacks the route goes undrained. Spans keep being
    /// spooled meanwhile, so an upgrade delivers them.</summary>
    public static readonly TimeSpan RouteMissingPause = TimeSpan.FromHours(24);

    const string PauseFile = ".route-missing-until";

    public WorkspaceSpool(ConfigRoot config, string? server, TimeProvider time, Action<string>? warn = null)
        : this(SpoolPartition.Server(config.Path(DirectoryName), server), time, warn) { }

    public string DirectoryPath => partition.DirectoryPath;

    string? SessionDir(string sessionId) => HookSpool.KeyFor(sessionId) is { } key ? Path.Combine(partition.DirectoryPath, key) : null;

    static string FileName(Guid spanId) => $"{spanId:N}.json";

    public bool Has(string sessionId, Guid spanId) => SessionDir(sessionId) is { } dir && File.Exists(Path.Combine(dir, FileName(spanId)));

    /// <summary>Spools one span, deleting the session's oldest span files whole to stay under the cap.</summary>
    public bool Write(WorkspaceChangesRequest request) {
        if (SessionDir(request.SessionId) is not { } dir) return false;

        var json = JsonSerializer.Serialize(request, WorkspaceJsonContext.Default.WorkspaceChangesRequest);
        MakeRoom(dir, Encoding.UTF8.GetByteCount(json));

        return WorkspaceFiles.WriteAtomic(Path.Combine(dir, FileName(request.SpanId)), json);
    }

    void MakeRoom(string dir, long incoming) {
        try {
            if (!Directory.Exists(dir)) return;

            var files = Pending(dir).ToList();
            var total = files.Sum(f => f.Length);
            foreach (var file in files) {
                if (total + incoming <= capBytes) break;

                WorkspaceFiles.TryDelete(file.FullName);
                total -= file.Length;
                warn?.Invoke($"[kcap] workspace spool over {capBytes / (1024 * 1024)} MiB for session {Path.GetFileName(dir)}; dropped the oldest span {file.Name}");
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The next write makes room again.
        }
    }

    static IEnumerable<FileInfo> Pending(string dir) =>
        new DirectoryInfo(dir).EnumerateFiles("*.json")
            .OrderBy(f => f.LastWriteTimeUtc)
            .ThenBy(f => f.Name, StringComparer.Ordinal);

    public void DeleteExpired() => partition.DeleteExpired(time);

    /// <summary>Whether any span waits to be posted, so an empty spool costs no credential read.</summary>
    public bool HasPending {
        get {
            try {
                return Directory.Exists(partition.DirectoryPath)
                    && Directory.EnumerateDirectories(partition.DirectoryPath).Any(d => Directory.EnumerateFiles(d, "*.json").Any());
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return false;
            }
        }
    }

    public bool IsPaused {
        get {
            try {
                var path = Path.Combine(partition.DirectoryPath, PauseFile);
                return File.Exists(path)
                    && DateTimeOffset.TryParse(File.ReadAllText(path).Trim(), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var until)
                    && until > time.GetUtcNow();
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return false;
            }
        }
    }

    void MarkRouteMissing() =>
        WorkspaceFiles.WriteAtomic(Path.Combine(partition.DirectoryPath, PauseFile), (time.GetUtcNow() + RouteMissingPause).ToString("O", CultureInfo.InvariantCulture));

    /// <summary>Posts spooled spans oldest first until the spool is empty, the budget runs out, or a
    /// failure says a later attempt may succeed. Never throws.</summary>
    public async Task<WorkspaceDrainStop> DrainAsync(HttpClient client, string serverUrl, TimeSpan budget, CancellationToken ct = default) {
        if (IsPaused) return WorkspaceDrainStop.Paused;

        var started = time.GetTimestamp();
        TimeSpan Left() => budget - time.GetElapsedTime(started);

        List<FileInfo> files;
        try {
            files = Directory.Exists(partition.DirectoryPath)
                ? new DirectoryInfo(partition.DirectoryPath).EnumerateDirectories()
                    .SelectMany(d => d.EnumerateFiles("*.json"))
                    .OrderBy(f => f.LastWriteTimeUtc)
                    .ThenBy(f => f.FullName, StringComparer.Ordinal)
                    .ToList()
                : [];
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return WorkspaceDrainStop.Retryable;
        }

        var url          = serverUrl.TrimEnd('/') + RoutePath;
        var routePresent = false;

        foreach (var file in files) {
            if (Left() <= TimeSpan.Zero || ct.IsCancellationRequested) return WorkspaceDrainStop.Budget;

            string body;
            try {
                body = await File.ReadAllTextAsync(file.FullName, ct);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                continue;
            }

            using var content = new StringContent(body, Encoding.UTF8, "application/json");
            var status = await SendAsync(() => client.PostOnceAsync(url, content, time, Left(), ct));
            if (status is >= 200 and < 300) {
                WorkspaceFiles.TryDelete(file.FullName);
                continue;
            }

            if (status is null || HookSpool.IsRetryable(status.Value)) return WorkspaceDrainStop.Retryable;

            if (status == (int) HttpStatusCode.NotFound) {
                // A 404 is either a server without the route or the route refusing a deleted session.
                if (!routePresent) {
                    var probe = await SendAsync(() => client.GetOnceAsync(url, time, Left(), ct));
                    if (probe == (int) HttpStatusCode.NotFound) {
                        MarkRouteMissing();
                        return WorkspaceDrainStop.RouteMissing;
                    }
                    if (probe != (int) HttpStatusCode.NoContent) return WorkspaceDrainStop.ProbeUnsettled;

                    routePresent = true;
                }
            }

            WorkspaceFiles.TryDelete(file.FullName);
        }

        return WorkspaceDrainStop.Drained;
    }

    static async Task<int?> SendAsync(Func<Task<HttpResponseMessage>> send) {
        try {
            using var response = await send();
            return (int) response.StatusCode;
        } catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException) {
            return null;
        }
    }
}
