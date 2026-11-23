using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Copilot;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Core.Harness.Copilot;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Copilot has no subagent handling at all on its routed <c>AlreadyLoaded</c> path —
/// <c>SentChildContent</c> stays at its safe default <c>false</c> via the implicit
/// <see cref="ImportOutcome"/> conversion. A real Copilot AlreadyLoaded replay must be
/// recognized by <see cref="ImportCommand.IsLifecycleOnlyRoutedReplay"/> (vendor-neutral) and
/// suppressed so it doesn't double-count on top of the classify-time AlreadyLoaded bucket.
/// </summary>
public class CopilotImportSourceImportTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempHome] public required TempHome Home { get; init; }

    readonly WireMockServer _server  = WireMockServer.Start();
    readonly TempDir        _tmp     = new();
    readonly string         _tempDir;

    public CopilotImportSourceImportTests() => _tempDir = _tmp.Path;

    /// <summary>Copilot's layout rooted at the throwaway dir, so the session lands under the real
    /// <c>session-state/</c> name discovery walks.</summary>
    CopilotPaths CopilotLayout => new(new UserHome(_tempDir), xdgConfigHome: null, copilotHome: _tempDir);

    const string DashedSid = "11111111-2222-3333-4444-555555555555";

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    string WriteSession() {
        var dir = Path.Combine(CopilotLayout.SessionStateDir, DashedSid);
        Directory.CreateDirectory(dir);
        File.WriteAllLines(Path.Combine(dir, "events.jsonl"), new[] {
            $$"""{"type":"session.start","data":{"sessionId":"{{DashedSid}}"},"id":"e1","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}""",
            """{"type":"user.message","data":{"text":"hello"},"id":"e2","timestamp":"2026-06-10T20:23:50.000Z","parentId":"e1"}""",
            """{"type":"assistant.message","data":{"text":"hi there"},"id":"e3","timestamp":"2026-06-10T20:23:51.000Z","parentId":"e2"}"""
        });
        File.WriteAllText(Path.Combine(dir, "workspace.yaml"), "cwd: /work/a\nname: proj\nuser_named: true\n");
        return _tempDir;
    }

    [Test]
    public async Task A_session_the_server_deleted_is_counted_deleted_and_sent_nothing() {
        WriteSession();
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(410).WithBody("""{"error":"session_deleted"}"""));

        using var client = new HttpClient();
        var source = new CopilotImportSource(Config.Root, CopilotLayout, [],
            new GitProviderRouter(), TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home),
            CancellationToken.None);

        await Assert.That(classified.Single().Status).IsEqualTo(ImportCommand.ClassificationStatus.Deleted);
        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Method == "POST")).IsEqualTo(0);
    }

    [Test]
    public async Task ImportSession_AlreadyLoaded_replay_is_a_no_op_suppressed_by_the_vendor_neutral_gate() {
        WriteSession();

        // Server already covers every importable line → AlreadyLoaded.
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        foreach (var route in new[] { "/hooks/session-start/copilot", "/hooks/set-title", "/hooks/session-end/copilot" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }

        using var client = new HttpClient();
        var source = new CopilotImportSource(Config.Root, CopilotLayout, [],
            new GitProviderRouter(), TimeProvider.System);

        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(discovered.Count).IsEqualTo(1);

        var classified = await source.ClassifyAsync(
            discovered,
            new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home),
            CancellationToken.None);
        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        var result = await source.ImportSessionAsync(
            classified[0],
            new ImportContext(client, _server.Url!, ForcePrivate: false),
            CancellationToken.None);

        // Copilot never touches a child/subagent stream — SentChildContent stays false.
        await Assert.That(result.SentChildContent).IsFalse();

        var isSuppressed = ImportCommand.IsLifecycleOnlyRoutedReplay(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(isSuppressed).IsTrue();

        var resolved = ImportCommand.ResolveRoutedOutcomeForCounting(
            classified[0].Status, result.Outcome, result.SentChildContent);
        await Assert.That(resolved).IsNull();

        // /hooks/harness-title is unstubbed (bare 404 → RouteMissing), so the title falls back to
        // /hooks/set-title (stubbed above) — but the harness-title attempt still carries the kind a
        // user-named Copilot session forwards.
        var titleAttempt = _server.FindLogEntries(Request.Create().WithPath("/hooks/harness-title").UsingPost());
        await Assert.That(titleAttempt.Count).IsEqualTo(1);
        var titleBody = System.Text.Json.Nodes.JsonNode.Parse(titleAttempt[0].RequestMessage.Body!)!;
        await Assert.That(titleBody["kind"]!.GetValue<string>()).IsEqualTo("rename");
    }

    void StubServerHoldingEveryLine() {
        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":2}"""));
        foreach (var route in new[] { "/hooks/session-start/copilot", "/hooks/harness-title", "/hooks/set-title", "/hooks/session-end/copilot" }) {
            _server.Given(Request.Create().WithPath(route).UsingPost())
                .RespondWith(Response.Create().WithStatusCode(200));
        }
    }

    /// <summary>A chat VS Code moved from Local is imported with the marker naming the Local chat whose request has its
    /// first copied prompt's time and text, so the server links the two as the live watcher's marker does. The marker
    /// ends the copied history on the server, so it goes only once the server holds the resume that ends it.</summary>
    [Test]
    [Arguments(2, 200, false, true)]
    [Arguments(1, 200, false, false)]
    [Arguments(2, 422, false, false)]
    [Arguments(1, 200, true, true)]
    public async Task An_import_of_a_chat_moved_from_local_names_its_local_chat(int serverLastLine, int transcriptStatus, bool resumeHeldBack, bool marked) {
        const string localId = "fa0d6e8d-7c9d-446f-a93c-5a2a89e011b4";
        var cwd = Path.Combine(_tempDir, "work", "a");
        var dir = Path.Combine(CopilotLayout.SessionStateDir, DashedSid);
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(Path.Combine(dir, "events.jsonl"), new[] {
            $$$"""{"type":"session.start","data":{"sessionId":"{{{DashedSid}}}","producer":"vscode-copilot-migration","context":{"cwd":{{{System.Text.Json.JsonSerializer.Serialize(cwd)}}}}},"id":"e1","timestamp":"2026-11-18T17:51:27.398Z","parentId":null}""",
            """{"type":"user.message","data":{"content":"Add multiply."},"id":"e2","timestamp":"2026-11-18T17:51:27.408Z","parentId":"e1"}""",
            """{"type":"session.resume","data":{"context":{}},"id":"e3","timestamp":"2026-11-18T17:53:40.853Z","parentId":"e2"}""",
            """{"type":"user.message","data":{"content":"Add divide."},"id":"e4","timestamp":"2026-11-18T17:53:46.468Z","parentId":"e3"}""",
        });
        await File.WriteAllTextAsync(Path.Combine(dir, "workspace.yaml"), $"cwd: {cwd}\n");

        var storage   = Path.Combine(_tempDir, "workspaceStorage");
        var workspace = Path.Combine(storage, "ws1");
        Directory.CreateDirectory(Path.Combine(workspace, "chatSessions"));
        await File.WriteAllTextAsync(Path.Combine(workspace, "workspace.json"), $$"""{"folder":"{{new Uri(cwd).AbsoluteUri}}"}""");
        await File.WriteAllTextAsync(Path.Combine(workspace, "chatSessions", localId + ".jsonl"),
            $$$"""{"kind":0,"v":{"version":3,"sessionId":"{{{localId}}}","requests":[{"requestId":"request_1","timestamp":1795024287408,"message":{"text":"Add multiply."}}]}}""" + "\n");

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"last_line_number":{{serverLastLine}}}"""));
        foreach (var route in new[] { "/hooks/session-start/copilot", "/hooks/harness-title", "/hooks/set-title", "/hooks/session-end/copilot" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        // A rejected transcript batch stops a moved chat's send before its marker.
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(transcriptStatus).WithBody("{}"));

        using var client = new HttpClient();
        var source     = new CopilotImportSource(Config.Root, CopilotLayout, [storage], new GitProviderRouter(), TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);

        // A rewrite can keep the resume as a copy of a line the server already holds, which the numbering leaves unsent.
        var classification = classified.Single();
        if (resumeHeldBack)
            classification = classification with {
                SourceMeta = new Dictionary<string, object?>(classification.SourceMeta!) { ["Numbering"] = (Func<int, int?>)(line => line == 2 ? null : line) },
            };

        var outcome = await source.ImportSessionAsync(classification, new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(outcome.Outcome == ImportOutcome.Failed).IsEqualTo(!marked);
        var markers = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .Select(e => e.RequestMessage.Body ?? "")
            .Where(b => b.Contains(CopilotMovedChat.MarkerType, StringComparison.Ordinal))
            .ToList();

        await Assert.That(markers.Count).IsEqualTo(marked ? 1 : 0);
        if (marked) {
            await Assert.That(markers[0]).Contains(localId);
            await Assert.That(markers[0]).Contains(CopilotMovedChat.MarkerLineNumber.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }
    }

    /// <summary>A moved chat whose copied history ends only while the import sends it gets no marker from that run, which
    /// sent it without stopping at a lost batch: the import fails, and a re-run sends it with the marker.</summary>
    [Test]
    public async Task A_moved_history_that_ends_during_the_import_fails_it_for_a_rerun() {
        var dir  = Path.Combine(CopilotLayout.SessionStateDir, DashedSid);
        var path = Path.Combine(dir, "events.jsonl");
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(path, new[] {
            $$$"""{"type":"session.start","data":{"sessionId":"{{{DashedSid}}}","producer":"vscode-copilot-migration","context":{"cwd":"/work/a"}},"id":"e1","timestamp":"2026-11-18T17:51:27.398Z","parentId":null}""",
            """{"type":"user.message","data":{"content":"Add multiply."},"id":"e2","timestamp":"2026-11-18T17:51:27.408Z","parentId":"e1"}""",
        });
        await File.WriteAllTextAsync(Path.Combine(dir, "workspace.yaml"), "cwd: /work/a\n");

        _server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":-1}"""));
        foreach (var route in new[] { "/hooks/session-start/copilot", "/hooks/harness-title", "/hooks/set-title", "/hooks/session-end/copilot" })
            _server.Given(Request.Create().WithPath(route).UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        // The resume lands in the file as the transcript is sent.
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}").WithCallback(_ => {
                File.AppendAllText(path, """{"type":"session.resume","data":{"context":{}},"id":"e3","timestamp":"2026-11-18T17:53:40.853Z","parentId":"e2"}""" + "\n");
                return new WireMock.ResponseMessage { StatusCode = 200, BodyData = new WireMock.Util.BodyData { BodyAsString = "{}", DetectedBodyType = WireMock.Types.BodyType.String } };
            }));

        using var client = new HttpClient();
        var source     = new CopilotImportSource(Config.Root, CopilotLayout, [], new GitProviderRouter(), TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);

        var outcome = await source.ImportSessionAsync(classified.Single(), new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(File.ReadAllText(path)).Contains("session.resume");
        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Failed);
    }

    /// <summary>The import reads Copilot's name through the same store the live watcher polls, so a name that only
    /// repeats the first prompt is left for the server's own title.</summary>
    [Test]
    public async Task An_import_sends_no_title_when_copilots_name_only_repeats_the_first_prompt() {
        var dir = Path.Combine(CopilotLayout.SessionStateDir, DashedSid);
        Directory.CreateDirectory(dir);
        await File.WriteAllLinesAsync(Path.Combine(dir, "events.jsonl"), new[] {
            $$"""{"type":"session.start","data":{"sessionId":"{{DashedSid}}"},"id":"e1","timestamp":"2026-06-10T20:23:49.371Z","parentId":null}""",
            """{"type":"user.message","data":{"content":"Where does copper-lantern, the new receipt printer, keep its queue?"},"id":"e2","timestamp":"2026-06-10T20:23:50.000Z","parentId":"e1"}""",
            """{"type":"assistant.message","data":{"content":"In the spooler"},"id":"e3","timestamp":"2026-06-10T20:23:51.000Z","parentId":"e2"}"""
        });
        await File.WriteAllTextAsync(Path.Combine(dir, "workspace.yaml"), "cwd: /work/a\nname: Where does copper-lantern, the new recei\nuser_named: false\n");
        StubServerHoldingEveryLine();

        using var client = new HttpClient();
        var source     = new CopilotImportSource(Config.Root, CopilotLayout, [], new GitProviderRouter(), TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, _server.Url!, MinLines: 0, Home: Home), CancellationToken.None);

        await source.ImportSessionAsync(classified.Single(), new ImportContext(client, _server.Url!, ForcePrivate: false), CancellationToken.None);

        await Assert.That(_server.FindLogEntries(Request.Create().WithPath("/hooks/harness-title").UsingPost()).Count).IsEqualTo(0);
        await Assert.That(_server.FindLogEntries(Request.Create().WithPath("/hooks/set-title").UsingPost()).Count).IsEqualTo(0);
    }

    /// <summary>A re-import of a session the server already holds whole claims no repository backfill: the CLI cannot
    /// tell whether the replayed start changed anything.</summary>
    [Test]
    public async Task A_reimport_of_a_loaded_session_claims_no_repository_backfill() {
        WriteSession();
        StubServerHoldingEveryLine();

        var source = new CopilotImportSource(Config.Root, CopilotLayout, [], new GitProviderRouter(), TimeProvider.System);
        var stdout = new StringWriter();

        var exitCode = await new ImportCommand(Config.Root, Resolutions.At(_server.Url!, Config.Root), Home, TestHarnesses.Under(Home),
                new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: TestImportConsole.Over(stdout))
            .HandleImport(filterCwd: null, minLines: 0, sources: [source], scope: new ImportScope.All(), skipConfirmation: true);

        await Assert.That(exitCode).IsEqualTo(0);
        await Assert.That(stdout.ToString()).Contains($"Refreshed {SessionIds.Display(DashedSid.Replace("-", ""))} (already loaded)");
        await Assert.That(stdout.ToString()).DoesNotContain("repository backfill");
    }
}
