using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.RepoInstructions;

namespace Capacitor.Cli.Recording.RepoInstructions;

/// <summary>Uploads a session's primary repository's instruction files once, as the default branch holds them, when their
/// digest differs from the one this server last accepted for the repository. Nothing is queued: an attempt that fails ends
/// it, except that a server which does not know the session's repository yet is asked again for a few minutes, and a rate
/// limit is waited out once. Each outcome is one log line.</summary>
public sealed class RepoInstructionsUploader(
        string                                                        sessionId,
        string                                                        serverUrl,
        ConfigRoot                                                    config,
        Func<Profile?>                                                profile,
        ICapacitorHttpClient                                          http,
        RepoInstructionsDigestCache                                   cache,
        TimeProvider                                                  time,
        Action<string>                                                log,
        Func<Func<Task<HttpResponseMessage>>, Task<HttpResponseMessage>>? carry = null,
        Func<string, Action, CancellationToken, Task<RepoInstructionsRead>>? read = null) {
    /// <summary>How long a server that does not know the session's repository yet is asked again.</summary>
    internal static readonly TimeSpan UnknownRepositoryWindow = TimeSpan.FromMinutes(5);

    internal static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(1);

    // Settable so a test can run the real clock through a retry.
    internal TimeSpan FirstBackoff { get; init; } = TimeSpan.FromSeconds(10);

    // Half the watcher's heartbeat threshold: the post runs inline in the watch loop.
    internal static readonly TimeSpan PostBudget = TimeSpan.FromSeconds(10);

    static readonly TimeSpan DefaultRetryAfter = TimeSpan.FromMinutes(1);

    readonly Func<string, Action, CancellationToken, Task<RepoInstructionsRead>> _read =
        read ?? ((root, beat, ct) => RepoInstructionsReader.ReadAsync(root, time, ct, beat));

    (string RepoHash, string Label, RepoInstructionSet Set)? _pending;
    DateTimeOffset  _nextAttempt = DateTimeOffset.MinValue;
    DateTimeOffset? _firstUnknown;
    int             _unknownAttempts;
    bool            _rateLimited;

    public bool Done { get; private set; }

    /// <summary>Every attempt for <paramref name="repository"/>, waiting out each retry, until one ends it or
    /// <paramref name="ct"/> does. Meant to run beside the watch loop, never in it.</summary>
    public async Task RunAsync(RepositoryPayload repository, string? root, CancellationToken ct) {
        while (!Done) {
            if (_nextAttempt - time.GetUtcNow() is var wait && wait > TimeSpan.Zero) await Task.Delay(wait, time, ct);
            await TickAsync(repository, root, () => { }, ct);
        }
    }

    /// <param name="repository">The session's primary repository; nothing happens until it is detected.</param>
    /// <param name="root">The checkout it was detected in.</param>
    internal async Task TickAsync(RepositoryPayload? repository, string? root, Action beat, CancellationToken ct) {
        if (Done || time.GetUtcNow() < _nextAttempt || repository is not { Owner: { } owner, RepoName: { } name }) return;

        try {
            if (_pending is null && !await PrepareAsync(owner, name, root, beat, ct)) return;
            await PostAsync(beat, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            Finish($"Repo instructions not uploaded: {ex.Message}");
        }
    }

    async Task<bool> PrepareAsync(string owner, string name, string? root, Action beat, CancellationToken ct) {
        var label   = LocalRepository.Label(owner, name);
        var current = profile();

        if (current?.DisableRepoInstructionsUpload is true) return Finish("Repo instructions not uploaded: disable_repo_instructions_upload is set");
        if (root is null) return Finish($"Repo instructions not uploaded for {label}: no checkout to read them from");
        if (RepoScope.IsOutOfScope(GitRepoKey.ScopeKeys(root, config, serverUrl), GitRepoKey.ExclusionKeys(root, config), current?.AllowedRepos, current?.ExcludedRepos))
            return Finish($"Repo instructions not uploaded for {label}: the repository is outside allowed_repos or in excluded_repos");

        var found = await _read(root, beat, ct);
        if (found.Set is not { } set) return Finish($"Repo instructions not uploaded for {label}: {found.Unavailable}");

        foreach (var skip in set.Skipped) log($"Repo instruction file skipped: {skip}");
        if (set.OverLimits) return Finish($"Repo instructions not uploaded for {label}: its root instruction files alone exceed the upload limits");

        beat();
        string? hash;
        using (var deadline = new CancellationTokenSource(PostBudget, time))
        using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token)) {
            using var client = await http.ForBackgroundAsync(linked.Token);
            hash = await RepositoryHashes.ForAsync(client, serverUrl, owner, name, ct: linked.Token);
        }
        if (hash is null) return Finish($"Repo instructions not uploaded for {label}: the repository's hash could not be resolved");
        if (cache.Read(serverUrl, hash) == set.Digest) return Finish($"Repo instructions for {label} unchanged since they were last uploaded");

        _pending = (hash, label, set);
        return true;
    }

    async Task PostAsync(Action beat, CancellationToken ct) {
        var (hash, label, set) = _pending!.Value;

        beat();
        var       started  = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(PostBudget, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        using var client   = await http.ForBackgroundAsync(linked.Token);

        var body = JsonSerializer.Serialize(new RepoInstructionsUploadRequest(sessionId, set.Digest, set.Files),
            RepoInstructionsJsonContext.Default.RepoInstructionsUploadRequest);
        using var content  = new StringContent(body, Encoding.UTF8, "application/json");
        var       remaining = PostBudget - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) {
            Finish($"Repo instructions not uploaded for {label}: signing in took the whole budget");
            return;
        }
        Task<HttpResponseMessage> Post() => client.PostOnceAsync($"{serverUrl.TrimEnd('/')}/api/repos/instructions", content, time, remaining, linked.Token);
        using var response = await (carry is null ? Post() : carry(Post));

        var now = time.GetUtcNow();
        switch (response.StatusCode) {
            case HttpStatusCode.OK:
                var status = await ReadAsync(response, RepoInstructionsJsonContext.Default.RepoInstructionsUploadResponse, ct);
                cache.Write(serverUrl, hash, set.Digest);
                Finish($"Repo instructions for {label} {status?.Status ?? "uploaded"}: {set.Files.Count} file(s)");
                return;
            case HttpStatusCode.ServiceUnavailable:
                _firstUnknown ??= now;
                if (now - _firstUnknown >= UnknownRepositoryWindow) {
                    Finish($"Repo instructions not uploaded for {label}: the server did not know the session's repository within {UnknownRepositoryWindow.TotalMinutes:0} minutes");
                    return;
                }
                _nextAttempt = now + Backoff(_unknownAttempts++);
                return;
            case HttpStatusCode.TooManyRequests when !_rateLimited:
                _rateLimited = true;
                _nextAttempt = now + RetryDelay(response.Headers.RetryAfter, now);
                return;
            default:
                var problem = await ReadAsync(response, RepoInstructionsJsonContext.Default.RepoInstructionsProblem, ct);
                Finish($"Repo instructions not uploaded for {label}: HTTP {(int)response.StatusCode}"
                     + (problem?.Title is { } title ? $" {title}" : "") + (problem?.Detail is { } detail ? $": {detail}" : ""));
                return;
        }
    }

    static TimeSpan RetryDelay(System.Net.Http.Headers.RetryConditionHeaderValue? retryAfter, DateTimeOffset now) {
        var delay = retryAfter?.Delta ?? (retryAfter?.Date is { } date ? date - now : null);
        return delay > TimeSpan.Zero ? delay.Value : DefaultRetryAfter;
    }

    TimeSpan Backoff(int attempt) {
        var delay = FirstBackoff * Math.Pow(2, Math.Min(attempt, 10));
        return delay < MaxBackoff ? delay : MaxBackoff;
    }

    bool Finish(string line) {
        Done = true;
        log(line);
        return false;
    }

    static async Task<T?> ReadAsync<T>(HttpResponseMessage response, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type, CancellationToken ct) where T : class {
        try {
            return JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), type);
        } catch (JsonException) {
            return null;
        }
    }
}
