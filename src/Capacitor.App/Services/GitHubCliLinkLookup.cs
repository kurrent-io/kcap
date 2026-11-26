using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core.PullRequests.Readers;
using Capacitor.Cli.Core.PullRequests.Readers.GitHubCli;

namespace Capacitor.App.Services;

/// Reads a GitHub pull request's or issue's state with the signed-in GitHub CLI the pull request
/// reader already uses, so a private repository answers and the account's own rate limit applies.
/// Without a signed-in `gh` nothing is asked: GitHub's anonymous limit is a few dozen requests an
/// hour shared by every program on the machine, too few to spend on chat links. One call reads
/// both kinds, since GitHub's issue endpoint also answers for a pull request.
public sealed class GitHubCliLinkLookup(GitHubCliRunner cli, IPullRequestReaderProvider reader, TimeProvider time) {
    /// Once GitHub refuses for the rate limit, nothing is asked until this has passed.
    static readonly TimeSpan RateLimitHold = TimeSpan.FromMinutes(10);
    const int OutputLimit = 64 * 1024;
    long _heldUntil;

    public async Task<GitHubLinkState?> LookUpAsync(GitHubLink link, CancellationToken ct) {
        if (time.GetUtcNow().UtcTicks < Interlocked.Read(ref _heldUntil)) return null;
        await reader.ProbeAsync(false, ct).ConfigureAwait(false);
        if (!reader.Serves("github", "github.com")) return null;
        var path = $"repos/{link.Owner}/{link.Repo}/issues/{link.Number.ToString(CultureInfo.InvariantCulture)}";
        var result = await cli.RunAsync(["api", "--hostname", "github.com", path, "--jq", "{state: .state, draft: .draft, merged_at: .pull_request.merged_at}"],
            OutputLimit, ct).ConfigureAwait(false);
        if (result.Outcome == GitHubCliOutcome.Ok) return Parse(result.Stdout);
        if (result.Stderr.Contains("rate limit", StringComparison.OrdinalIgnoreCase)) Interlocked.Exchange(ref _heldUntil, (time.GetUtcNow() + RateLimitHold).UtcTicks);
        return null;
    }

    /// The state in `{state, draft, merged_at}`; null for anything else.
    public static GitHubLinkState? Parse(string json) {
        try {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return root.Str("state") switch {
                "open" => root.Bool("draft") == true ? GitHubLinkState.Draft : GitHubLinkState.Open,
                "closed" => root.Str("merged_at") is { Length: > 0 } ? GitHubLinkState.Merged : GitHubLinkState.Closed,
                _ => null,
            };
        } catch (JsonException) {
            return null;
        }
    }
}
