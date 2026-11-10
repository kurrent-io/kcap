using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class RepoPayloadChangedTests {
    static RepositoryPayload MakePayload(
            string owner    = "o",
            string repo     = "r",
            string branch   = "main",
            int?   prNumber = 1,
            string prUrl    = "u",
            string prTitle  = "t"
        ) => new() { Owner = owner, RepoName = repo, Branch = branch, PrNumber = prNumber, PrUrl = prUrl, PrTitle = prTitle };

    [Test]
    public async Task NullCurrent_ReturnsFalse() =>
        await Assert.That(TranscriptRecorder.RepoPayloadChanged(null, MakePayload())).IsFalse();

    [Test]
    public async Task NullLastSent_ReturnsTrue() =>
        await Assert.That(TranscriptRecorder.RepoPayloadChanged(MakePayload(), null)).IsTrue();

    [Test]
    public async Task BothNull_ReturnsFalse() =>
        await Assert.That(TranscriptRecorder.RepoPayloadChanged(null, null)).IsFalse();

    [Test]
    public async Task SameValues_ReturnsFalse() =>
        await Assert.That(TranscriptRecorder.RepoPayloadChanged(MakePayload(), MakePayload())).IsFalse();

    [Test]
    [Arguments("Owner")]
    [Arguments("RepoName")]
    [Arguments("Branch")]
    [Arguments("PrNumber")]
    [Arguments("PrUrl")]
    [Arguments("PrTitle")]
    public async Task DifferentField_ReturnsTrue(string field) {
        var a = MakePayload();

        var b = field switch {
            "Owner"    => a with { Owner = "x" },
            "RepoName" => a with { RepoName = "x" },
            "Branch"   => a with { Branch = "x" },
            "PrNumber" => a with { PrNumber = 99 },
            "PrUrl"    => a with { PrUrl = "x" },
            "PrTitle"  => a with { PrTitle = "x" },
            _          => a
        };
        await Assert.That(TranscriptRecorder.RepoPayloadChanged(a, b)).IsTrue();
    }

    [Test]
    public async Task NonComparedFields_DoNotTriggerChange() {
        var a = MakePayload() with { UserName = "alice" };
        var b = MakePayload() with { UserName = "bob" };
        await Assert.That(TranscriptRecorder.RepoPayloadChanged(a, b)).IsFalse();
    }
}
