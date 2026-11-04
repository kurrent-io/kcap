using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Only the daemon's native resume of its own agent reopens a session: the vendor process restarts on
/// the entry's vendor session, and the session it names is the entry's.</summary>
public class ResumedSessionOfTests {
    static readonly ResumeLedgerEntry Entry =
        ResumeLedgerEntries.For("agent-1", ResumeState.Resuming) with { SessionId = "session-1", VendorSession = "vendor-1" };

    static readonly ResumeLedgerEntry ParticipantEntry = Entry with {
        Participant = new ParticipantResume(LaunchKind.ReviewFlow, null, null, null, 1, 0, null, null)
    };

    public static IEnumerable<Func<(string Case, ResumeLedgerEntry? Entry, string? ResumeSessionId, string? Expected)>> Cases() {
        yield return () => ("native resume", Entry, "vendor-1", "session-1");
        yield return () => ("continuation", Entry, null, null);
        yield return () => ("participant resume", ParticipantEntry, "vendor-1", null);
        yield return () => ("fresh launch resuming a session", null, "vendor-1", null);
        yield return () => ("another vendor session", Entry, "vendor-2", null);
        yield return () => ("entry with no vendor session", Entry with { VendorSession = null }, null, null);
    }

    [Test]
    [MethodDataSource(nameof(Cases))]
    public async Task Names_the_entrys_session_only_for_a_native_resume(
            (string Case, ResumeLedgerEntry? Entry, string? ResumeSessionId, string? Expected) row) =>
        await Assert.That(AgentOrchestrator.ResumedSessionOf(row.Entry, row.ResumeSessionId)).IsEqualTo(row.Expected);
}
