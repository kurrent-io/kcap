using Capacitor.Cli.Core;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class ApplyEvidenceScanTests {
    static string? FakeFindRoot(string dir) => dir.StartsWith("/h/dev/repo-r", StringComparison.Ordinal) ? "/h/dev/repo-r" : null;

    static Task<RepositoryPayload?> FakeResolve(string root) =>
        Task.FromResult<RepositoryPayload?>(new() { Owner = "acme", RepoName = "repo-r" });

    static WatchState NewOutsideRepoState() => new() {
        EvidenceScanner = new RepoEvidenceScanner<RepositoryPayload>(
            FakeFindRoot, FakeResolve, p => p.Owner is not null && p.RepoName is not null)
    };

    static readonly string[] ReadOnlyLine = [
        """{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/h/dev/repo-r/y.cs"}}]}}""",
    ];

    // The read-only fallback must reach state.Repository specifically on the FINAL drain, since that's
    // the only batch that still goes out on a clean session end.
    [Test]
    public async Task FinalDrain_delivers_the_read_fallback_to_state_repository() {
        var state = NewOutsideRepoState();

        await TranscriptRecorder.ApplyEvidenceScanAsync(state, "claude", ReadOnlyLine, isFinalDrain: true);

        await Assert.That(state.Repository).IsNotNull();
        await Assert.That(state.Repository!.RepoName).IsEqualTo("repo-r");
        await Assert.That(state.RepositoryFromEvidence).IsTrue();
    }

    [Test]
    public async Task NonFinalDrain_does_not_promote_the_read_fallback() {
        var state = NewOutsideRepoState();

        await TranscriptRecorder.ApplyEvidenceScanAsync(state, "claude", ReadOnlyLine, isFinalDrain: false);

        await Assert.That(state.Repository).IsNull();
    }
}
