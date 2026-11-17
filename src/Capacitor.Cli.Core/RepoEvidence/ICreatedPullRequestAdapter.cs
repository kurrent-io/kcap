namespace Capacitor.Cli.Core.RepoEvidence;

/// <summary>Reads one vendor's raw transcript lines for PR creations. Stateless: whatever spans lines lives in the
/// watcher's <see cref="CreatedPullRequestEvidence"/>. Fail-open — a line it cannot read yields nothing.</summary>
public interface ICreatedPullRequestAdapter {
    void Observe(string line, CreatedPullRequestEvidence evidence);
}
