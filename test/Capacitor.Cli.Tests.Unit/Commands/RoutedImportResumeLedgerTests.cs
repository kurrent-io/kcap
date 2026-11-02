using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.MistralVibe;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RoutedImportResumeLedgerTests : IDisposable {
    const string SessionId = "a1b2c3d4e5f60718293a4b5c6d7e8f90";

    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    readonly EventStoringTranscriptServer _server = new();

    public void Dispose() => _server.Dispose();

    static string Content(string vendor) => vendor switch {
        "cursor" => """{"type":"assistant","message":{"content":"hello"}}""",
        "mistral-vibe" => """{"role":"assistant","content":"hello"}""",
        _ => """{"payload_type":"runtime.session","recorded_at":1791916402415040,"payload":{"kind":"run","event":{"kind":"started","prompt":"hello"}}}""",
    };

    static string Tail(string vendor) => vendor switch {
        "cursor" => """{"type":"turn_ended","status":"success"}""",
        "mistral-vibe" => """{"role":"system","content":"trailing system record"}""",
        _ => """{"payload_type":"session.end","recorded_at":1791916437749511,"payload":{"kind":"session_end","record":{"exit_reason":"clean"}}}""",
    };

    (IImportSource Source, string Log) CreateSession(string vendor) {
        var lines = new[] {
            vendor == "muse"
                ? """{"payload_type":"runtime.session.metadata","recorded_at":1791916401897928,"payload":{"record":{"workspace_root":"/tmp/proj"}}}"""
                : Content(vendor),
            Content(vendor),
            "",
            Tail(vendor),
        };

        switch (vendor) {
            case "cursor":
                return (new CursorImportSource(Config.Root, Tmp.PathTo("projects"), Tmp.PathTo("workspaces"),
                    router: new GitProviderRouter(), time: TimeProvider.System),
                    Tmp.CreateFile($"projects/tmp-proj/agent-transcripts/{SessionId}/{SessionId}.jsonl", lines));
            case "mistral-vibe":
                Tmp.CreateFile("vibe/logs/session/session_20261102/meta.json", $$"""{"session_id":"{{SessionId}}","cwd":"/tmp/proj"}""");
                return (new MistralVibeImportSource(new MistralVibePaths(Home, Tmp.PathTo("vibe")), TimeProvider.System),
                    Tmp.CreateFile("vibe/logs/session/session_20261102/messages.jsonl", lines));
            default:
                return (new MuseImportSource(new MusePaths(Home, null, Tmp.PathTo("data")), new WatcherPaths(Tmp.PathTo("watchers")), TimeProvider.System),
                    Tmp.CreateFile($"data/muse/sessions/2026/11/02/{SessionId}/session.jsonl", lines));
        }
    }

    async Task<string> Import(IImportSource source, bool reimport = false, EventStoringTranscriptServer? server = null) {
        var terminal = new TestImportTerminal();
        var exit = await new ImportCommand(Config.Root, Resolutions.At((server ?? _server).Url, Config.Root), Home, TestHarnesses.Under(Home),
                new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: terminal.Console)
            .HandleImport(filterCwd: null, minLines: 1, sources: [source], scope: new ImportScope.All(), skipConfirmation: true, reimport: reimport, skipTitle: true);
        await Assert.That(exit).IsEqualTo(0);
        return terminal.Plain.ToString();
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task unchanged_ignored_tail_is_already_loaded_on_every_reimport(string vendor) {
        var (source, _) = CreateSession(vendor);
        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([0, 1, 3]);

        for (var run = 0; run < 2; run++) {
            _server.ClearPosted();
            var again = await Import(source);
            await Assert.That(again).Contains("0 imported · 1 skipped");
            await Assert.That(_server.Posted).IsEmpty();
        }
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task appended_lines_resume_after_the_acknowledged_ignored_tail(string vendor) {
        var (source, log) = CreateSession(vendor);
        await Import(source);
        File.AppendAllText(log, Content(vendor) + "\n" + Tail(vendor) + "\n");
        _server.ClearPosted();

        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([4, 5]);

        _server.ClearPosted();
        await Import(source);
        await Assert.That(_server.Posted).IsEmpty();
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task a_changed_server_watermark_invalidates_the_local_frontier(string vendor) {
        var (source, _) = CreateSession(vendor);
        await Import(source);
        _server.TruncateAbove(SessionId, 0);
        _server.ClearPosted();

        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([1, 3]);
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task an_incomplete_last_line_is_retried_after_its_newline_arrives(string vendor) {
        var (source, log) = CreateSession(vendor);
        File.AppendAllText(log, Tail(vendor));
        await Import(source);
        File.AppendAllText(log, "\n");
        _server.ClearPosted();

        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([4]);

        _server.ClearPosted();
        await Import(source);
        await Assert.That(_server.Posted).IsEmpty();
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task a_mark_for_one_server_does_not_skip_another_servers_transcript(string vendor) {
        var (source, _) = CreateSession(vendor);
        await Import(source);
        using var other = new EventStoringTranscriptServer();

        await Import(source, server: other);
        await Assert.That(other.Posted).IsEquivalentTo([0, 1, 3]);
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task a_refused_batch_does_not_acknowledge_the_ignored_tail(string vendor) {
        var (source, log) = CreateSession(vendor);
        await Import(source);
        File.AppendAllText(log, Tail(vendor) + "\n");
        _server.RejectTranscripts = true;
        await Import(source);
        _server.RejectTranscripts = false;
        _server.ClearPosted();

        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([4]);
    }

    [Test]
    [Arguments("cursor")]
    [Arguments("mistral-vibe")]
    [Arguments("muse")]
    public async Task explicit_reimport_replays_the_tail_from_the_server_watermark(string vendor) {
        var (source, _) = CreateSession(vendor);
        await Import(source);
        _server.ClearPosted();

        await Import(source, reimport: true);
        await Assert.That(_server.Posted).IsEquivalentTo([3]);
    }
}
