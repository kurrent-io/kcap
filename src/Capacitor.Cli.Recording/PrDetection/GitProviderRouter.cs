using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Recording.PrDetection;

internal enum GitProviderKind { GitHub, GitLab, Unknown }

/// <summary>
/// Maps a remote host to a provider. SaaS hosts route directly; a custom host is
/// probed via `gh auth status --json hosts` (GitHub if listed, else best-effort GitLab),
/// and the answer is remembered for a while.
///
/// <para>Injected, never constructed at a call site: one per process is what makes remembering
/// worth anything. A recorder re-detects every 60s for as long as it runs, so a custom host would
/// otherwise pay for that probe on every refresh. A process can run for weeks, so an answer expires, and
/// a probe that failed says nothing about the host, so what gh last answered stands and otherwise GitLab
/// is a guess, kept either way for a few refreshes.</para>
///
/// <para>Public because <c>kcap</c> injects it across the assembly boundary; everything it does
/// stays internal.</para>
/// </summary>
public sealed class GitProviderRouter(TimeProvider? time = null) {
    internal static readonly TimeSpan AnswerLifetime      = TimeSpan.FromHours(1);
    internal static readonly TimeSpan FailedProbeLifetime = TimeSpan.FromMinutes(5);

    readonly TimeProvider _time = time ?? TimeProvider.System;

    readonly ConcurrentDictionary<string, (GitProviderKind Kind, bool Answered, DateTimeOffset Until)> _memo = new(StringComparer.OrdinalIgnoreCase);

    internal async Task<GitProviderKind> ResolveAsync(string? host, string cwd, TimeSpan cap, CommandRunner run) {
        if (string.IsNullOrEmpty(host)) return GitProviderKind.Unknown;
        if (host == "github.com") return GitProviderKind.GitHub;
        if (host == "gitlab.com") return GitProviderKind.GitLab;

        if (_memo.TryGetValue(host, out var remembered) && _time.GetUtcNow() < remembered.Until) return remembered.Kind;

        var (kind, answered) = await ProbeAsync(host, cwd, cap, run);

        if (answered) {
            _memo[host] = (kind, true, _time.GetUtcNow() + AnswerLifetime);
            return kind;
        }

        var kept = remembered.Answered ? remembered.Kind : kind;
        _memo[host] = (kept, remembered.Answered, _time.GetUtcNow() + FailedProbeLifetime);
        return kept;
    }

    static async Task<(GitProviderKind Kind, bool Answered)> ProbeAsync(string host, string cwd, TimeSpan cap, CommandRunner run) {
        // `gh auth status --json hosts` forces exit 0 even on auth failure, so decide
        // from the JSON payload, not the exit code.
        var json = await run("gh", "auth status --json hosts", cwd, cap);
        if (json is not null) {
            try {
                if (JsonNode.Parse(json)?["hosts"] is JsonObject hosts) {
                    // Not a known GitHub host → assume GitLab and let the detector no-op if unauthenticated.
                    return (hosts.ContainsKey(host) ? GitProviderKind.GitHub : GitProviderKind.GitLab, true);
                }
            } catch { /* fall through */ }
        }
        return (GitProviderKind.GitLab, false);
    }
}
