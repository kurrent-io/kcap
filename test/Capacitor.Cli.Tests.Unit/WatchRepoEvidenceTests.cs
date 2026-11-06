using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit;

public class WatchRepoEvidenceTests {
    [Test]
    public async Task Refresh_never_clears_an_evidence_payload() {
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: null, repositoryFromEvidence: true)).IsFalse();
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: null, repositoryFromEvidence: false)).IsTrue();
        await Assert.That(WatchCommand.ShouldReplaceRepository(detected: new(), repositoryFromEvidence: true)).IsTrue();
    }

    // File.ReadLines opens FileShare.Read (Windows-mandatory), which can block the
    // agent's own flush of the transcript it's actively writing. ReadLinesShared is the
    // FileShare.ReadWrite replacement (mirrors File.ReadAllTextShared's rationale) —
    // this just guards it still reads lines correctly; the sharing behavior itself is
    // Windows-specific and not meaningfully observable from a cross-platform unit test.
    [Test]
    public async Task ReadLinesShared_reads_lines_from_a_real_file() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("transcript.jsonl", ["line one", "line two"]);

        var lines = File.ReadLinesShared(path).ToList();

        await Assert.That(lines).IsEquivalentTo(["line one", "line two"]);
    }
}
