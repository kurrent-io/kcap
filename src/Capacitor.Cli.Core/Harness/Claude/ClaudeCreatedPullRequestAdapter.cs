using System.Text.Json;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// The github.com pull request a Claude transcript line records the agent creating, read from the
/// <c>gitOperation</c> annotation Claude Code puts on a tool result. It holds whichever checkout
/// the PR came from — a `gh pr create --repo` run anywhere, a subagent's worktree, one of several
/// branches in a shared worktree — none of which a probe of a checked-out branch can see.
/// </summary>
public sealed class ClaudeCreatedPullRequestAdapter : ICreatedPullRequestAdapter {
    public static ClaudeCreatedPullRequestAdapter Instance { get; } = new();

    ClaudeCreatedPullRequestAdapter() { }

    public void Observe(string line, CreatedPullRequestEvidence evidence) {
        if (FromLine(line) is { } pr) evidence.Created(pr, null);
    }

    public static CreatedPullRequest? FromLine(string jsonlLine) {
        if (!jsonlLine.Contains("\"gitOperation\"", StringComparison.Ordinal)) return null;

        try {
            using var doc = JsonDocument.Parse(jsonlLine);

            if (doc.RootElement.Obj("toolUseResult")?.Obj("gitOperation")?.Obj("pr") is not { } pr
             || pr.Str("action") != "created"
             || pr.Str("url") is not { } url
             || !Uri.TryCreate(url, UriKind.Absolute, out var uri)
             || !string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)
             || !PrRefParser.TryParse(url, out var owner, out var name, out var number)
             || pr.Num("number") is { } declared && declared != number) {
                return null;
            }

            return new CreatedPullRequest(owner, name, number, url);
        } catch (JsonException) {
            return null;
        }
    }
}
