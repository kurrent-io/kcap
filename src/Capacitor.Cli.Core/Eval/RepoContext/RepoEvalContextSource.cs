using System.Text.Json;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.RepoInstructions;

namespace Capacitor.Cli.Core.Eval.RepoContext;

/// <summary>The <see cref="RepoEvalContextBlock"/> for a session's primary repository: its instruction files read from this
/// machine's checkout, since the server never sends the ones it stores, and the memories section the server renders. With
/// neither there is no block.</summary>
public sealed class RepoEvalContextSource(LocalRepoCheckouts checkouts, TimeProvider time) {
    public static RepoEvalContextSource For(ConfigRoot config, TimeProvider time) =>
        new(new LocalRepoCheckouts(new RepoPathStore(config, time).GetSortedPathsAsync), time);

    public async Task<string> ForSessionAsync(HttpClient client, string baseUrl, string sessionId, IEvalObserver observer, CancellationToken ct) {
        var files    = await InstructionFilesAsync(client, baseUrl, sessionId, observer, ct);
        var memories = await RepoEvalMemoriesClient.ForSessionAsync(client, baseUrl, sessionId, observer, time, ct);

        return files.Count == 0 && memories.Length == 0 ? "" : RepoEvalContextBlock.Render(files, time.GetUtcNow(), memories);
    }

    async Task<IReadOnlyList<RepoInstructionFile>> InstructionFilesAsync(HttpClient client, string baseUrl, string sessionId, IEvalObserver observer,
            CancellationToken ct) {
        try {
            if (await RepoHashAsync(client, baseUrl, sessionId, ct) is not { } hash) return [];
            if (await checkouts.FindAsync(hash) is not { } root) {
                observer.OnInfo("no local checkout of the session's repository; judging without its instructions");
                return [];
            }

            var read = await RepoInstructionsReader.ReadAsync(root, time, ct);
            if (read.Set is { } set) return set.Files;

            observer.OnInfo($"repository instructions not read from {root}: {read.Unavailable}");
            return [];
        } catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested) {
            observer.OnInfo($"repository instructions not read: {ex.Message}");
            return [];
        }
    }

    async Task<string?> RepoHashAsync(HttpClient client, string baseUrl, string sessionId, CancellationToken ct) {
        using var response = await client.GetWithRetryAsync($"{baseUrl.TrimEnd('/')}/api/sessions/{Uri.EscapeDataString(sessionId)}/summary", time, ct: ct);
        if (!response.IsSuccessStatusCode) return null;

        try {
            var summary = JsonSerializer.Deserialize(await response.Content.ReadAsStringAsync(ct), CapacitorJsonContext.Default.SessionSummaryDto);
            if (summary?.Repositories.FirstOrDefault(r => r.IsPrimary) is { } primary && RepoHashHelper.IsRepoHash(primary.RepoHash)) return primary.RepoHash;

            return summary is { RepoOwner: { } owner, RepoName: { } name } && !LocalRepository.IsLocal(owner) ? RepoHashHelper.ComputeRepoHash(owner, name) : null;
        } catch (JsonException) {
            return null;
        }
    }
}
