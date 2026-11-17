using System.Globalization;
using System.Text.RegularExpressions;

namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>
/// One watcher's record of the <c>gh pr create</c> runs its transcript shows, fed by its vendor's
/// <see cref="ICreatedPullRequestAdapter"/>. A run counts as creating a PR only when it succeeded and the last
/// non-empty line of its output is exactly one github.com PR URL, of the <c>--repo</c> it named if it named one.
/// That turns away gh's "already exists" (a failure), a <c>--dry-run</c>, and a URL the agent only wrote in prose.
/// </summary>
public sealed partial class CreatedPullRequestEvidence(int capacity = 16) {
    readonly Dictionary<string, string?> _pending = new(StringComparer.Ordinal);
    readonly List<string>                _order   = [];
    readonly List<FoundPullRequest>      _found   = [];

    /// <summary>A tool call whose result arrives in a later record. Only a <c>gh pr create</c> is kept, and only
    /// the newest <c>capacity</c> of those.</summary>
    public void Call(string callId, string command) {
        if (_pending.ContainsKey(callId) || !PrCreateCommand.TryMatch(command, out var repo)) return;

        if (_order.Count >= capacity) {
            _pending.Remove(_order[0]);
            _order.RemoveAt(0);
        }

        _pending[callId] = repo;
        _order.Add(callId);
    }

    /// <summary>The result of a call <see cref="Call"/> saw. Any other call's result is ignored.</summary>
    public void Result(string callId, string? output, bool succeeded) {
        if (!_pending.Remove(callId, out var repo)) return;

        _order.Remove(callId);
        if (succeeded) Accept(callId, output, repo);
    }

    /// <summary>A record that carries a command and its result together.</summary>
    public void Completed(string? callId, string command, string? output, bool succeeded) {
        if (succeeded && PrCreateCommand.TryMatch(command, out var repo)) Accept(callId, output, repo);
    }

    /// <summary>A PR the harness itself records as created.</summary>
    public void Created(CreatedPullRequest pr, string? callId) => _found.Add(new(pr, callId));

    /// <summary>The PRs found since the last call.</summary>
    public IReadOnlyList<FoundPullRequest> Drain() {
        if (_found.Count == 0) return [];

        var found = _found.ToArray();
        _found.Clear();
        return found;
    }

    void Accept(string? callId, string? output, string? repo) {
        if (LastLinePullRequest(output) is not { } pr) return;
        if (repo is not null && !PrCreateCommand.RepoMatches(repo, pr.Owner, pr.RepoName)) return;

        _found.Add(new(pr, callId));
    }

    static CreatedPullRequest? LastLinePullRequest(string? output) {
        if (string.IsNullOrEmpty(output)) return null;

        var span = output.AsSpan().TrimEnd();
        var line = span[(span.LastIndexOf('\n') + 1)..].Trim();

        var match = PullRequestUrl().Match(line.ToString());
        if (!match.Success || !int.TryParse(match.Groups["number"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var number)) return null;

        return new(match.Groups["owner"].Value, match.Groups["repo"].Value, number, match.Value);
    }

    [GeneratedRegex(@"^https://github\.com/(?<owner>[A-Za-z0-9][A-Za-z0-9-]*)/(?<repo>[A-Za-z0-9._-]+)/pull/(?<number>[1-9][0-9]*)$", RegexOptions.IgnoreCase)]
    private static partial Regex PullRequestUrl();
}
