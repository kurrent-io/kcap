using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

public class CopilotRunBoundaryTests {
    const string Start  = """{"type":"session.start","data":{},"id":"evt-1","timestamp":1}""";
    const string Turn   = """{"type":"assistant.message","data":{"content":"session.resume"},"id":"evt-2","timestamp":2}""";
    const string Resume = """{"type":"session.resume","data":{},"id":"evt-3","timestamp":3}""";

    [TempDir] public required TempDir Tmp { get; init; }

    /// <summary>The boundary is the byte offset of the latest run line, and only a line of that type counts.</summary>
    [Test]
    public async Task The_boundary_is_where_the_latest_run_began() {
        var transcript  = Tmp.CreateFile("events.jsonl", Start + "\n" + Turn + "\n" + Resume + "\n" + Turn + "\n");
        var resumeStart = (long) System.Text.Encoding.UTF8.GetByteCount(Start + "\n" + Turn + "\n");

        await Assert.That(CopilotRunBoundary.Latest(transcript)).IsEqualTo(resumeStart);
        await Assert.That(CopilotRunBoundary.Latest(transcript, upTo: resumeStart)).IsEqualTo(0L);
    }

    [Test]
    public async Task No_transcript_or_no_run_line_names_no_boundary() {
        await Assert.That(CopilotRunBoundary.Latest(Tmp.PathTo("missing.jsonl"))).IsNull();
        await Assert.That(CopilotRunBoundary.Latest(Tmp.CreateFile("turns.jsonl", Turn + "\n"))).IsNull();
    }
}
