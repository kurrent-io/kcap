using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Core;

/// <summary>
/// The hash a client reads a repository's data by. A repository with a remote hashes its owner and name, as every
/// client and the server compute it. One with no remote is stored under an owner the server binds to whoever sent
/// it, which no client can compute, so the server is asked; a client that cannot ask reads nothing for it, never
/// the unbound hash, which names no stored repository.
/// </summary>
public static class RepositoryHashes {
    /// <summary>What a command says when the server could not name this repository with no remote.</summary>
    public const string Unresolved = "This repository has no remote, and the server could not resolve it; it may need updating.";

    public static Task<string?> ForAsync(
            HttpClient client, string baseUrl, string owner, string name, Func<HttpResponseMessage, Task>? onFailure = null,
            Action<HttpRequestException>? onUnreachable = null, CancellationToken ct = default) =>
        ForAsync((url, token) => client.GetAsync(url, token), baseUrl, owner, name, onFailure, onUnreachable, ct);

    /// <param name="get">Sends a GET to the absolute URL it is given.</param>
    /// <param name="onFailure">Sees a refused or failed lookup before it reads as unresolved, so a caller can tell an
    /// older server's 404 from a sign-in it must renew or a server that failed.</param>
    /// <param name="onUnreachable">Sees a lookup that never reached the server.</param>
    public static async Task<string?> ForAsync(
            Func<string, CancellationToken, Task<HttpResponseMessage>> get, string baseUrl, string owner, string name,
            Func<HttpResponseMessage, Task>? onFailure = null,
            Action<HttpRequestException>? onUnreachable = null,
            CancellationToken ct = default) {
        if (!LocalRepository.IsLocal(owner)) return RepoHashHelper.ComputeRepoHash(owner, name);

        var url = $"{baseUrl.TrimEnd('/')}/api/repositories/resolve?owner={Uri.EscapeDataString(owner)}&name={Uri.EscapeDataString(name)}";

        try {
            using var response = await get(url, ct);
            if (!response.IsSuccessStatusCode) {
                if (onFailure is not null) await onFailure(response);
                return null;
            }

            var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(ct));

            // The hash names files on this machine, so a value of any other shape is never used.
            return body?["repo_hash"] is JsonValue value && value.TryGetValue(out string? hash) && RepoHashHelper.IsRepoHash(hash) ? hash : null;
        } catch (HttpRequestException e) {
            onUnreachable?.Invoke(e);
            return null;
        } catch (Exception e) when (e is JsonException or InvalidOperationException
                                     || (e is OperationCanceledException && !ct.IsCancellationRequested)) {
            return null;
        }
    }
}
