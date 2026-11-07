using Capacitor.Cli.Commands;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// Re-importing against a server whose watermark only counts lines that produced an event. A Claude
/// transcript ends in lines the server ignores (<c>system</c>, <c>last-prompt</c>), so its watermark stays
/// below the transcript's last line however often the tail is resent.
/// </summary>
public class ImportResumeLedgerTests : IDisposable {
    const string SessionId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    readonly EventStoringTranscriptServer _server = new();

    public void Dispose() => _server.Dispose();

    static string Turn(string type, int i) =>
        $$$"""{"type":"{{{type}}}","timestamp":"2026-03-15T10:00:0{{{i}}}Z","cwd":"/tmp/proj","message":{"content":"step {{{i}}}"}}""";

    static readonly string[] CompletedSession = [
        Turn("user", 0),
        Turn("assistant", 1),
        Turn("user", 2),
        Turn("assistant", 3),
        """{"type":"system","subtype":"turn_duration","timestamp":"2026-03-15T10:00:04Z"}""",
        """{"type":"last-prompt","lastPrompt":"step 2"}""",
    ];

    string WriteTranscript(string[] lines) =>
        Tmp.CreateDir("projects", "-tmp-proj").CreateFile($"{SessionId}.jsonl", lines);

    async Task<string> Import() {
        var terminal = new TestImportTerminal();
        var exit = await new ImportCommand(Config.Root, Resolutions.At(_server.Url, Config.Root), Home, TestHarnesses.Under(Home),
                          new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System,
                          console: terminal.Console)
            .HandleImport(
                filterCwd:        null,
                minLines:         1,
                sources:          [new ClaudeImportSource(Config.Root, Tmp.PathTo("projects"), router: new GitProviderRouter(), time: TimeProvider.System)],
                scope:            new ImportScope.All(),
                skipConfirmation: true,
                skipTitle:        true);

        await Assert.That(exit).IsEqualTo(0);

        return terminal.Plain.ToString();
    }

    [Test]
    public async Task unchanged_transcript_is_already_loaded_on_every_reimport() {
        WriteTranscript(CompletedSession);

        var first = await Import();
        await Assert.That(first).Contains("1 imported · 0 skipped");
        await Assert.That(_server.Posted).IsEquivalentTo(Enumerable.Range(0, 6));
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(4);

        for (var run = 0; run < 2; run++) {
            _server.ClearPosted();

            var again = await Import();

            await Assert.That(again).Contains("0 imported · 1 skipped");
            await Assert.That(again).DoesNotContain("resuming");
            await Assert.That(_server.Posted).IsEmpty();
            await Assert.That(_server.SessionEnds).IsEqualTo(0);
            await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(4);
        }
    }

    /// <summary>The server hides the session from <c>last-line</c> for the read right after session-end, as its
    /// projection does under a burst of imports; the hidden read must have been the import's own, or this proves nothing.</summary>
    [Test]
    public async Task a_watermark_hidden_right_after_the_import_is_still_recorded() {
        WriteTranscript(CompletedSession);
        _server.HiddenReadsAfterEnd = 1;

        await Import();
        await Assert.That(_server.HiddenReadsAfterEnd).IsEqualTo(0);
        _server.ClearPosted();

        var again = await Import();

        await Assert.That(again).Contains("0 imported · 1 skipped");
        await Assert.That(again).DoesNotContain("resuming");
        await Assert.That(_server.Posted).IsEmpty();
    }

    /// <summary>Three hidden reads across two sessions: both first reads, then one session's first retry, so that
    /// session sees two 404s in a row and is settled only by the round after the other one caught up.</summary>
    [Test]
    public async Task a_watermark_hidden_twice_is_recorded_once_another_session_catches_up() {
        WriteTranscript(CompletedSession);
        Tmp.CreateDir("projects", "-tmp-proj").CreateFile("b1b2c3d4e5f60718293a4b5c6d7e8f90.jsonl", CompletedSession);
        _server.HiddenReadsAfterEnd = 3;

        await Import();
        await Assert.That(_server.HiddenReadsAfterEnd).IsEqualTo(0);
        _server.ClearPosted();

        var again = await Import();

        await Assert.That(again).Contains("0 imported · 2 skipped");
        await Assert.That(again).DoesNotContain("resuming");
        await Assert.That(_server.Posted).IsEmpty();
    }

    [Test]
    public async Task appended_lines_resume_from_the_first_line_never_sent() {
        var path = WriteTranscript(CompletedSession);
        await Import();

        File.AppendAllText(path, Turn("user", 6) + "\n" + """{"type":"system","subtype":"turn_duration"}""" + "\n");
        _server.ClearPosted();

        var resumed = await Import();

        await Assert.That(resumed).Contains("resuming from line 6");
        await Assert.That(_server.Posted).IsEquivalentTo([6, 7]);
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(5);

        _server.ClearPosted();
        var again = await Import();

        await Assert.That(again).Contains("0 imported · 1 skipped");
        await Assert.That(_server.Posted).IsEmpty();
    }

    [Test]
    public async Task a_server_that_lost_lines_since_the_import_resumes_from_its_own_watermark() {
        WriteTranscript(CompletedSession);
        await Import();

        _server.TruncateAbove(SessionId, 1);
        _server.ClearPosted();

        var resumed = await Import();

        await Assert.That(resumed).Contains("resuming from line 2");
        await Assert.That(_server.Posted).IsEquivalentTo([2, 3, 4, 5]);
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(4);
    }

    [Test]
    public async Task a_last_line_without_its_newline_is_sent_again_once_it_has_one() {
        var path = WriteTranscript(CompletedSession);
        File.AppendAllText(path, """{"type":"system","subtype":"turn_duration"}""");
        await Import();

        File.AppendAllText(path, "\n");
        _server.ClearPosted();

        var resumed = await Import();

        await Assert.That(resumed).Contains("resuming from line 6");
        await Assert.That(_server.Posted).IsEquivalentTo([6]);
    }
}
