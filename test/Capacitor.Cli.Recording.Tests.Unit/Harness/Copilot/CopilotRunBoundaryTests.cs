using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

public class CopilotRunBoundaryTests {
    const string Start  = """{"type":"session.start","data":{},"id":"run-a","timestamp":1}""";
    const string Turn   = """{"type":"assistant.message","data":{"content":"session.resume"},"id":"evt-2","timestamp":2}""";
    const string Resume = """{"type":"session.resume","data":{},"id":"run-b","timestamp":3}""";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>A run is named by the id of its own start or resume line, and only a line of that type names one.</summary>
    [Test]
    public async Task The_runs_are_named_by_their_boundary_lines_ids() {
        var runs = CopilotRunBoundary.Read(Tmp.CreateFile("events.jsonl", Start + "\n" + Turn + "\n" + Resume + "\n" + Turn + "\n"))!;

        await Assert.That(runs.LatestId).IsEqualTo("run-b");
        await Assert.That(runs.Ids).IsEquivalentTo(["run-a", "run-b"]);
    }

    [Test]
    public async Task No_transcript_or_no_run_line_names_no_run() {
        await Assert.That(CopilotRunBoundary.Read(Tmp.PathTo("missing.jsonl"))).IsNull();
        await Assert.That(CopilotRunBoundary.LatestIdOf(Tmp.CreateFile("turns.jsonl", Turn + "\n"))).IsNull();
    }
}
