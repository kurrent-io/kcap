using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Antigravity;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Kiro;
using Capacitor.Cli.Core.Harness.MistralVibe;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Harness.Antigravity;
using Capacitor.Cli.Harness.Claude;
using Capacitor.Cli.Harness.Codex;
using Capacitor.Cli.Harness.Copilot;
using Capacitor.Cli.Harness.Cursor;
using Capacitor.Cli.Harness.Gemini;
using Capacitor.Cli.Harness.Grok;
using Capacitor.Cli.Harness.Kiro;
using Capacitor.Cli.Harness.MistralVibe;
using Capacitor.Cli.Harness.Muse;
using Capacitor.Cli.Harness.OpenCode;
using Capacitor.Cli.Harness.Pi;
using Capacitor.Cli.Recording.PrDetection;
using Capacitor.Cli.Tests.Unit.Harness.OpenCode;

namespace Capacitor.Cli.Tests.Unit.Commands;

[ParallelLimiter<SubprocessLimit>]
public class ImportExclusionSourcesTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    const string Id = "11111111-2222-4333-8444-555555555555";
    const string Timestamp = "2026-10-13T10:00:00Z";
    const string BaseUrl = "http://import.test";
    static readonly JsonSerializerOptions BootstrapJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    [Test]
    [Arguments("claude")]
    [Arguments("codex")]
    [Arguments("cursor")]
    [Arguments("copilot")]
    [Arguments("gemini")]
    [Arguments("kiro")]
    [Arguments("pi")]
    [Arguments("opencode")]
    [Arguments("antigravity")]
    [Arguments("mistral-vibe")]
    [Arguments("grok")]
    [Arguments("muse")]
    public async Task An_excluded_session_makes_no_request_and_stays_excluded_offline(string vendor) {
        using var db = vendor == "opencode" ? new OpenCodeDbFixture() : null;
        var cwd = Tmp.CreateDir("excluded-project").Path;
        if (vendor == "cursor") {
            cwd = cwd.Replace('\\', '/');
            // Cursor's encoded directory must stay a legal Windows filename.
            if (cwd.Length > 1 && cwd[1] == ':') cwd = cwd[2..];
        }
        var source = Fixture(vendor, cwd, db);
        var sessions = await source.DiscoverAsync(new(null, null, null, 1), CancellationToken.None);
        await Assert.That(sessions.Count).IsEqualTo(1);

        // A valid, importable fixture must reach a real probe when its scope admits it.
        using var control = new SequencedHttpScript(SequencedHttpScript.Reply(HttpStatusCode.NotFound));
        using var controlClient = new HttpClient(control, disposeHandler: false);
        var included = await source.ClassifyAsync(sessions, new(controlClient, BaseUrl, 1, Home), CancellationToken.None);
        await Assert.That(included.Single().Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(control.Count).IsEqualTo(1);
        if (vendor != "antigravity") await Assert.That(included.Single().Meta.Cwd).IsEqualTo(cwd);

        var allowed = Tmp.CreateDir("allowed-project").Path;
        var profile = vendor == "antigravity"
            ? new Core.Config.Profile { AllowedPaths = [allowed] }
            : new Core.Config.Profile { ExcludedPaths = [cwd] };
        foreach (var offline in new[] { false, true }) {
            using var requests = new SequencedHttpScript(offline
                ? _ => Task.FromException<HttpResponseMessage>(new HttpRequestException("server unreachable"))
                : SequencedHttpScript.Reply(HttpStatusCode.NotFound));
            var terminal = new TestImportTerminal();
            ImportCommand.ImportRunOutcome? outcome = null;
            var import = new ImportCommand(Config.Root,
                Resolutions.Of(profile, "exclusion-test", BaseUrl), Home, TestHarnesses.Under(Home),
                new RecordingCapacitorHttpClient(requests), new GitProviderRouter(), TimeProvider.System,
                console: terminal.Console);

            var exit = await import.HandleImport(null, minLines: 1, sources: [source], scope: new ImportScope.All(),
                skipConfirmation: true, skipTitle: true, onFinished: o => outcome = o);

            await Assert.That(exit).IsEqualTo(0);
            await Assert.That(requests.Count).IsEqualTo(0);
            await Assert.That(outcome!.Counts.Excluded).IsEqualTo(1);
            await Assert.That(outcome.Counts.Imported).IsEqualTo(0);
            await Assert.That(outcome.Counts.Failed).IsEqualTo(0);
            await Assert.That(terminal.Plain.ToString()).Contains("Excluded          1");
        }

        using var shortRequests = new SequencedHttpScript(SequencedHttpScript.Reply(HttpStatusCode.NotFound));
        ImportCommand.ImportRunOutcome? shortOutcome = null;
        var shortImport = new ImportCommand(Config.Root,
            Resolutions.Of(profile, "exclusion-test", BaseUrl), Home, TestHarnesses.Under(Home),
            new RecordingCapacitorHttpClient(shortRequests), new GitProviderRouter(), TimeProvider.System,
            console: new TestImportTerminal().Console);
        var shortExit = await shortImport.HandleImport(null, minLines: 100, sources: [source], scope: new ImportScope.All(),
            skipConfirmation: true, skipTitle: true, onFinished: o => shortOutcome = o);

        await Assert.That(shortExit).IsEqualTo(0);
        await Assert.That(shortRequests.Count).IsEqualTo(0);
        await Assert.That(shortOutcome!.Counts.TooShort).IsEqualTo(1);
        await Assert.That(shortOutcome.Counts.Excluded).IsEqualTo(0);
    }

    IImportSource Fixture(string vendor, string cwd, OpenCodeDbFixture? db) {
        var cwdJson = JsonSerializer.Serialize(cwd);
        var router = new GitProviderRouter();
        var time = TimeProvider.System;

        switch (vendor) {
            case "claude":
                Tmp.CreateFile($"claude/projects/-excluded-project/{Id}.jsonl",
                    $$$$"""{"type":"user","timestamp":"{{{{Timestamp}}}}","cwd":{{{{cwdJson}}}},"message":{"content":"hello"}}""" + "\n");
                return new ClaudeImportSource(Config.Root, Tmp.PathTo("claude", "projects"), router, time);
            case "codex":
                Tmp.CreateFile($"codex/sessions/2026/10/13/rollout-2026-10-13T10-00-00-{Id}.jsonl", [
                    $$$$"""{"type":"session_meta","timestamp":"{{{{Timestamp}}}}","payload":{"id":"{{{{Id}}}}","cwd":{{{{cwdJson}}}}}}""",
                    $$$$"""{"type":"response_item","timestamp":"{{{{Timestamp}}}}","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"hello"}]}}""",
                ]);
                return new CodexImportSource(Config.Root, Tmp.PathTo("codex", "sessions"), router, time);
            case "cursor":
                var encoded = CursorImportSource.EncodeWorkspacePath(cwd);
                Tmp.CreateFile($"cursor/projects/{encoded}/agent-transcripts/{Id}/{Id}.jsonl", "{\"role\":\"user\",\"message\":{\"content\":[{\"type\":\"text\",\"text\":\"hello\"}]}}\n");
                Tmp.CreateFile("cursor/workspaces/hash/workspace.json", JsonSerializer.Serialize(new { folder = "file://" + cwd }));
                return new CursorImportSource(Config.Root, Tmp.PathTo("cursor", "projects"), Tmp.PathTo("cursor", "workspaces"), router, time);
            case "copilot":
                var copilot = new CopilotPaths(Home, Tmp.PathTo("copilot"));
                WriteUnder(copilot.SessionStateDir, $"{Id}/workspace.yaml", $"id: {Id}\ncwd: '{cwd.Replace("'", "''")}'\ncreated_at: {Timestamp}\n");
                WriteUnder(copilot.SessionStateDir, $"{Id}/events.jsonl", $$$$"""{"type":"user.message","timestamp":"{{{{Timestamp}}}}","data":{"content":"hello"}}""" + "\n");
                return new CopilotImportSource(Config.Root, copilot, [], router, time);
            case "gemini":
                var bootstrap = JsonSerializer.Serialize($"<session_context>\nThis is the Gemini CLI.\n- **Workspace Directories:**\n  - {cwd}\n- **Directory Structure:**\n</session_context>",
                    BootstrapJson);
                Tmp.CreateFile("gemini/tmp/project/chats/session-test.jsonl", [
                    $$$$"""{"sessionId":"{{{{Id}}}}","startTime":"{{{{Timestamp}}}}","kind":"main"}""",
                    $$$$"""{"type":"user","timestamp":"{{{{Timestamp}}}}","content":[{"text":{{{{bootstrap}}}}}]}""",
                ]);
                return new GeminiImportSource(Tmp.PathTo("gemini", "tmp"), time);
            case "kiro":
                Tmp.CreateFile($"kiro/{Id}.json", $$$$"""{"cwd":{{{{cwdJson}}}},"created_at":"{{{{Timestamp}}}}"}""");
                Tmp.CreateFile($"kiro/{Id}.jsonl", "{\"version\":\"v1\",\"kind\":\"Prompt\",\"data\":{\"message_id\":\"m1\",\"content\":[{\"kind\":\"text\",\"data\":\"hello\"}]}}\n");
                return new KiroImportSource(Config.Root, Tmp.PathTo("kiro"), new KiroCrewPaths(Tmp.PathTo("kiro"), null), router, time);
            case "pi":
                Tmp.CreateFile($"pi/{Id}.jsonl", [
                    $$$$"""{"type":"session","version":3,"id":"{{{{Id}}}}","timestamp":"{{{{Timestamp}}}}","cwd":{{{{cwdJson}}}}}""",
                    $$$$"""{"type":"message","id":"a1","timestamp":"{{{{Timestamp}}}}","message":{"role":"user","content":"hello"}}""",
                ]);
                return new PiImportSource(Config.Root, Tmp.PathTo("pi"), router, time);
            case "opencode":
                db!.AddSession("ses_root", null, cwd, "Hello", 1791885600000);
                db.AddMessageWithText("ses_root", "msg_1", "hello", 1791885600001);
                return new OpenCodeImportSource(db.DbPath, db.LedgerPath, time);
            case "antigravity":
                Tmp.CreateFile($".gemini/antigravity/brain/{Id}/.system_generated/logs/transcript_full.jsonl",
                    $$$$"""{"step_index":0,"source":"USER_EXPLICIT","type":"USER_INPUT","status":"DONE","created_at":"{{{{Timestamp}}}}","content":"<USER_REQUEST>hello</USER_REQUEST>"}""" + "\n");
                return new AntigravityImportSource(new AntigravityPaths(new UserHome(Tmp.Path), ""), time);
            case "mistral-vibe":
                Tmp.CreateFile($"vibe/logs/session/unified/{Id}/meta.json", $$$$"""{"origin_directory":{{{{cwdJson}}}}}""");
                Tmp.CreateFile($"vibe/logs/session/unified/{Id}/CURRENT", "{\"generation\":\"1\"}");
                Tmp.CreateFile($"vibe/logs/session/unified/{Id}/generations/1/manifest.json", "{\"projection_state\":{\"path\":\"projection.json\"}}");
                Tmp.CreateFile($"vibe/logs/session/unified/{Id}/generations/1/projection.json",
                    $$$$"""{"snapshot":{"history":{"entries":[{"id":"m1","type":"message","role":"user","content":"hello","createdAt":"{{{{Timestamp}}}}"}]}}}""");
                return new MistralVibeImportSource(new MistralVibePaths(Home, Tmp.PathTo("vibe")), time);
            case "grok":
                var grok = new GrokPaths(Home, Tmp.PathTo("grok"));
                var dir = Path.Combine(Uri.EscapeDataString(cwd), Id);
                WriteUnder(grok.SessionsDir, Path.Combine(dir, "summary.json"), $$$$"""{"info":{"id":"{{{{Id}}}}","cwd":{{{{cwdJson}}}}},"created_at":"{{{{Timestamp}}}}"}""");
                WriteUnder(grok.SessionsDir, Path.Combine(dir, "updates.jsonl"), "{\"timestamp\":1,\"method\":\"session/update\",\"params\":{\"sessionId\":\"x\",\"update\":{\"sessionUpdate\":\"user_message_chunk\",\"content\":{\"type\":\"text\",\"text\":\"hello\"}},\"_meta\":{\"eventId\":\"x-1\",\"agentTimestampMs\":1791885600000}}}\n");
                return new GrokImportSource(grok, _ => Task.FromResult<RepositoryPayload?>(null), time);
            case "muse":
                var muse = new MusePaths(Home, null, Tmp.PathTo("muse-data"));
                WriteUnder(muse.SessionsDir, $"2026/10/13/{Id}/session.jsonl",
                    $$$$"""{"payload_type":"runtime.session.metadata","recorded_at":1791885600000000,"payload":{"record":{"workspace_root":{{{{cwdJson}}}}}}}""" + "\n"
                  + "{\"payload_type\":\"session.opened.observed\",\"recorded_at\":1791885600000001}\n");
                return new MuseImportSource(muse, new WatcherPaths(Tmp.PathTo("watchers")), time);
            default:
                throw new ArgumentOutOfRangeException(nameof(vendor));
        }
    }

    void WriteUnder(string root, string relative, string contents) =>
        Tmp.CreateFile(Path.GetRelativePath(Tmp.Path, Path.Combine(root, relative)), contents);
}
