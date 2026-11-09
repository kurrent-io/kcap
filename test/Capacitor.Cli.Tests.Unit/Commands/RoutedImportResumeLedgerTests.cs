using Capacitor.Cli.Core;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.MistralVibe;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.Recording.PrDetection;
using System.Text.Json.Nodes;

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

    [Test]
    [Arguments(3, false)]
    [Arguments(3, true)]
    [Arguments(2, false)]
    public async Task cursor_replaced_ignored_lines_resume_from_the_server_watermark(int replacedLine, bool append) {
        var (source, log) = CreateSession("cursor");
        await Import(source);
        var lines = File.ReadAllLines(log);
        lines[replacedLine] = Content("cursor");
        File.WriteAllLines(log, lines);
        if (append) File.AppendAllText(log, Tail("cursor") + "\n");
        _server.ClearPosted();

        await Import(source);

        await Assert.That(_server.Posted).IsEquivalentTo(replacedLine == 2 ? [2, 3] : append ? [3, 4] : new[] { 3 });
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(3);
        _server.ClearPosted();
        await Import(source);
        await Assert.That(_server.Posted).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task cursor_marks_without_valid_content_evidence_replay_the_tail_once(bool malformed) {
        var (source, _) = CreateSession("cursor");
        await Import(source);
        var path = Config.Root.Path(TranscriptImportLedger.FileName);
        var ledger = JsonNode.Parse(File.ReadAllText(path))!;
        var mark = ledger[_server.Url]![SessionId]!.AsObject();
        if (malformed) mark["prefix_sha256"] = "invalid";
        else mark.Remove("prefix_sha256");
        File.WriteAllText(path, ledger.ToJsonString());
        _server.ClearPosted();

        await Import(source);

        await Assert.That(_server.Posted).IsEquivalentTo([3]);
        _server.ClearPosted();
        await Import(source);
        await Assert.That(_server.Posted).IsEmpty();
    }

    [Test]
    public async Task cursor_does_not_acknowledge_a_prefix_rewritten_after_delivery() {
        var (source, log) = CreateSession("cursor");
        _server.AfterTranscript = () => {
            _server.AfterTranscript = null;
            var lines = File.ReadAllLines(log);
            lines[3] = Content("cursor");
            File.WriteAllLines(log, lines);
        };
        await Import(source);
        _server.ClearPosted();

        await Import(source);

        await Assert.That(_server.Posted).IsEquivalentTo([3]);
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(3);
    }

    [Test]
    public async Task cursor_does_not_send_past_a_skipped_prefix_rewritten_after_classification() {
        var (source, log) = CreateSession("cursor");
        await Import(source);
        File.AppendAllText(log, Content("cursor") + "\n");
        _server.ClearPosted();
        _server.AfterSessionStart = () => {
            _server.AfterSessionStart = null;
            var lines = File.ReadAllLines(log);
            lines[3] = Content("cursor");
            File.WriteAllLines(log, lines);
        };

        await Import(source);

        await Assert.That(_server.Posted).IsEmpty();
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(2);
        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([3, 4]);
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(4);
    }

    [Test]
    [Arguments("remove_entry")]
    [Arguments("set_history_entries")]
    [Arguments("replace_entry")]
    public async Task unified_vibe_history_changes_resume_from_the_server_watermark(string operation) {
        var session = $"vibe/logs/session/unified/{SessionId}";
        Tmp.CreateFile($"{session}/meta.json", """{"environment":{"working_directory":"/tmp/proj"}}""");
        var first   = UnifiedMessage("u0", "user");
        var second  = UnifiedMessage("a1", "assistant");
        var ignored = UnifiedMessage("ignored", "system");
        var journal = Tmp.CreateFile($"{session}/journal/0001.jsonl", new[] {
            UnifiedDelta(AppendEntry(first), AppendEntry(second), AppendEntry(ignored)),
        });
        var source = new MistralVibeImportSource(new MistralVibePaths(Home, Tmp.PathTo("vibe")), TimeProvider.System);
        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([0, 1, 2]);
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(2);

        var next = UnifiedMessage("u2", "user");
        var last = UnifiedMessage("a3", "assistant");
        var edit = operation switch {
            "remove_entry" => UnifiedDelta("""{"op":"remove_entry","id":"ignored"}""", AppendEntry(next), AppendEntry(last)),
            "replace_entry" => UnifiedDelta("""{"op":"replace_entry","id":"ignored","entry":""" + next + "}"),
            _ => UnifiedDelta("""{"op":"set_history_entries","entries":[""" + string.Join(",", first, second, next, last) + "]}"),
        };
        File.AppendAllText(journal, edit + "\n");
        _server.ClearPosted();

        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo(operation == "replace_entry" ? [2] : new[] { 2, 3 });
    }

    static string UnifiedMessage(string id, string role) =>
        $$"""{"type":"message","id":"{{id}}","role":"{{role}}","content":"hello","generationStatus":"completed"}""";

    static string AppendEntry(string entry) => """{"op":"append_entry","entry":""" + entry + "}";

    static string UnifiedDelta(params string[] operations) =>
        """{"type":"projection_delta","payload":{"delta":[""" + string.Join(",", operations) + "]}}";

    [Test]
    [Arguments("model_completed")]
    [Arguments("run.model.configured")]
    public async Task unfinished_muse_metadata_tails_remain_replayable(string recordType) {
        var (source, log) = CreateSession("muse");
        var metadata = File.ReadLines(log).First();
        var tail = recordType == "model_completed"
            ? MuseRun("usage", """{"kind":"model_completed","usage":{"input_tokens":100,"output_tokens":20}}""")
            : $$$$"""{"id":"model","stream":{"kind":"session","id":"{{{{SessionId}}}}"},"record_type":"event","payload_type":"run.model.configured","payload":{"record":{"model_id":"muse-spark-1.3"}}}""";
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, log), new[] {
            metadata,
            MuseRun("text-1", """{"kind":"assistant_message_committed","text":"first"}"""),
            tail,
        });
        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([0, 1, 2]);
        await Assert.That(_server.StoredEvents(SessionId)).IsEqualTo(2);
        File.AppendAllText(log, MuseRun("text-2", """{"kind":"assistant_message_committed","text":"next"}""") + "\n");
        _server.ClearPosted();

        await Import(source);
        await Assert.That(_server.Posted).IsEquivalentTo([2, 3]);
    }

    static string MuseRun(string id, string evt) =>
        $$"""{"id":"{{id}}","stream":{"kind":"session","id":"{{SessionId}}"},"record_type":"event","recorded_at":1791916402415040,"payload_type":"runtime.session","payload":{"kind":"run","run_id":"run-1","event":""" + evt + "}}";
}
