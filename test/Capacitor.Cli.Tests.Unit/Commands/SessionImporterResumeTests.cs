using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class SessionImporterResumeTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [TempDir] public required TempDir Tmp { get; init; }

    const string Early = "aaaaaaaaaaaaaaaa";
    const string Late  = "bbbbbbbbbbbbbbbb";

    void RespondOk(string path, int status = 200) =>
        _server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(status));

    /// <summary>A parent whose line 1 references <see cref="Early"/> and line 3 <see cref="Late"/>, both with transcripts on disk.</summary>
    async Task<string> WriteSessionAsync() {
        var name        = Guid.NewGuid().ToString("N");
        var sessionPath = Tmp.PathTo($"{name}.jsonl");
        var agentsDir   = Tmp.CreateDir(name, "subagents");

        await File.WriteAllLinesAsync(sessionPath, [
            """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"p0"}}""",
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Early}}}"}}""",
            """{"type":"user","timestamp":"2026-03-15T10:00:01Z","message":{"content":"p2"}}""",
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Late}}}"}}""",
            """{"type":"user","timestamp":"2026-03-15T10:00:02Z","message":{"content":"p4"}}""",
        ]);

        foreach (var agent in new[] { Early, Late }) {
            await File.WriteAllLinesAsync(agentsDir.PathTo($"agent-{agent}.jsonl"), [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"a"}}""",
            ]);
        }

        return sessionPath;
    }

    List<JsonNode> Bodies(string path) =>
        [.. _server.FindLogEntries(Request.Create().WithPath(path).UsingPost()).Select(e => JsonNode.Parse(e.RequestMessage.Body!)!)];

    [Test]
    public async Task Resume_sends_the_subagents_first_referenced_from_the_start_line_and_the_parent_tail() {
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");
        var sessionPath = await WriteSessionAsync();

        using var client = new HttpClient();
        var result = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
            TimeProvider.System, startLine: 2, strict: true);

        await Assert.That(result.AgentIds).IsEquivalentTo([Late]);
        await Assert.That(result.LinesSent).IsEqualTo(3);
        await Assert.That(result.CompleteLines).IsEqualTo(5);

        var started = Bodies("/hooks/subagent-start").Select(b => b["agent_id"]!.GetValue<string>()).ToList();
        await Assert.That(started).IsEquivalentTo([Late]);
        await Assert.That(Bodies("/hooks/subagent-start").Concat(Bodies("/hooks/subagent-stop")).All(b => b["strict"]?.GetValue<bool>() == true)).IsTrue();

        var parentLines = Bodies("/hooks/transcript")
            .Where(b => b["agent_id"] is null)
            .SelectMany(b => b["line_numbers"]!.AsArray().Select(n => n!.GetValue<int>()))
            .ToList();
        await Assert.That(parentLines).IsEquivalentTo([2, 3, 4]);
    }

    [Test]
    public async Task Strict_resume_throws_on_a_refused_parent_batch() {
        RespondOk("/hooks/transcript", 400);
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");
        var sessionPath = await WriteSessionAsync();

        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await SessionImporter.ImportSessionAsync(
                client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
                TimeProvider.System, startLine: 2, strict: true));
    }

    [Test]
    [Arguments("/hooks/subagent-start")]
    [Arguments("/hooks/transcript")]
    [Arguments("/hooks/subagent-stop")]
    public async Task Strict_resume_stops_before_the_parent_lines_after_a_refused_subagent(string refusedPath) {
        _server.Given(Request.Create().WithPath(refusedPath).UsingPost().WithBody(new JsonPartialMatcher($$"""{"agent_id":"{{Late}}"}""")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400));
        foreach (var path in new[] { "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop" }) {
            _server.Given(Request.Create().WithPath(path).UsingPost())
                .AtPriority(2)
                .RespondWith(Response.Create().WithStatusCode(200));
        }
        var sessionPath = await WriteSessionAsync();

        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await SessionImporter.ImportSessionAsync(
                client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
                TimeProvider.System, startLine: 2, strict: true));

        await Assert.That(ParentLines()).IsEquivalentTo([2]);
        await Assert.That(Bodies("/hooks/transcript").Any(b => b["agent_id"] is not null))
            .IsEqualTo(refusedPath != "/hooks/subagent-start");
        await Assert.That(Bodies("/hooks/subagent-stop").Count).IsEqualTo(refusedPath == "/hooks/subagent-stop" ? 1 : 0);
    }

    const string Compact = "acompact-cccccccc";

    List<int> ParentLines() =>
        [.. Bodies("/hooks/transcript")
            .Where(b => b["agent_id"] is null)
            .SelectMany(b => b["line_numbers"]!.AsArray().Select(n => n!.GetValue<int>()))];

    /// <summary>
    /// The unreferenced subagent goes after the whole parent, so a refusal must leave the parent's
    /// last batch unsent: otherwise the server holds every parent line and the retry never happens.
    /// </summary>
    [Test]
    [Arguments(150)]
    [Arguments(201)]
    public async Task A_refused_trailing_subagent_holds_the_last_parent_batch_until_a_retry_delivers_it(int lineCount) {
        var name        = Guid.NewGuid().ToString("N");
        var sessionPath = Tmp.PathTo($"{name}.jsonl");
        var agentsDir   = Tmp.CreateDir(name, "subagents");
        await File.WriteAllLinesAsync(sessionPath, Enumerable.Range(0, lineCount).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"p{{{i}}}"}}"""));
        await File.WriteAllLinesAsync(agentsDir.PathTo($"agent-{Compact}.jsonl"), [
            """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"c"}}""",
        ]);

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost().WithBody(new JsonPartialMatcher($$"""{"agent_id":"{{Compact}}"}""")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(200));
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");

        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await SessionImporter.ImportSessionAsync(
                client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
                TimeProvider.System, startLine: 1, strict: true));

        var held = ParentLines();
        await Assert.That(held.Max()).IsEqualTo(100);

        _server.ResetMappings();
        _server.ResetLogEntries();
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");

        var retried = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
            TimeProvider.System, startLine: held.Max() + 1, strict: true);

        await Assert.That(retried.AgentIds).IsEquivalentTo([Compact]);
        await Assert.That(Bodies("/hooks/subagent-stop").Select(b => b["agent_id"]!.GetValue<string>())).IsEquivalentTo([Compact]);
        await Assert.That(ParentLines()).IsEquivalentTo(Enumerable.Range(101, lineCount - 101));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Resume_resends_a_subagent_still_running_at_the_watermark_at_its_next_reference(bool interimMessage) {
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");

        var name        = Guid.NewGuid().ToString("N");
        var sessionPath = Tmp.PathTo($"{name}.jsonl");
        var agentsDir   = Tmp.CreateDir(name, "subagents");
        var nextReference = interimMessage
            ? $$$"""{"type":"user","message":{"content":"<agent-message from=\"{{{Late}}}\">interim update</agent-message>"}}"""
            : $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Late}}}"}}""";
        await File.WriteAllLinesAsync(sessionPath, [
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Early}}}"}}""",
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Late}}}"}}""",
            """{"type":"user","timestamp":"2026-03-15T10:00:01Z","message":{"content":"p2"}}""",
            nextReference,
        ]);
        foreach (var agent in new[] { Early, Late }) {
            await File.WriteAllLinesAsync(agentsDir.PathTo($"agent-{agent}.jsonl"), [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"a"}}""",
            ]);
        }

        using var client = new HttpClient();
        var result = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
            TimeProvider.System, startLine: 2, strict: true);

        await Assert.That(result.AgentIds).IsEquivalentTo([Late]);
        await Assert.That(ParentLines()).IsEquivalentTo([2, 3]);
        var child = Bodies("/hooks/transcript").Single(body => body["agent_id"] is not null);
        await Assert.That(child["line_numbers"]!.AsArray().Single()!.GetValue<int>()).IsEqualTo(0);
        var posts = _server.LogEntries.Select(e => e.RequestMessage.Path);
        await Assert.That(string.Join(" ", posts))
            .IsEqualTo("/hooks/transcript /hooks/subagent-start /hooks/transcript /hooks/subagent-stop /hooks/transcript");
    }

    [Test]
    public async Task A_parent_reference_completed_during_delivery_is_left_for_the_next_resume() {
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");

        var parent = Tmp.PathTo("growing.jsonl");
        var agent = Tmp.CreateDir("growing", "subagents").PathTo($"agent-{Early}.jsonl");
        const string reference = $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Early}}}"}}""";
        const string message = """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"line"}}""";
        const string later = """{"type":"user","timestamp":"2026-03-15T11:00:00Z","message":{"content":"later"}}""";
        await File.WriteAllLinesAsync(parent, new[] { reference }.Concat(Enumerable.Repeat(message, 101)));
        var split = reference.Length / 2;
        await File.AppendAllTextAsync(parent, reference[..split]);
        await File.WriteAllLinesAsync(agent, [message]);

        using var client = new HttpClient();
        var first = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, parent, "s1", new SessionMetadata(), encodedCwd: null, TimeProvider.System,
            new OnProgress(progress => {
                if (progress is BatchFlushed { AgentId: null, LinesAdded: 100 })
                    File.AppendAllLines(parent, [reference[split..], later]);
            }), startLine: 1, strict: true);
        await Assert.That(first.AgentIds).IsEmpty();
        await Assert.That(first.TailDeferred).IsTrue();
        await Assert.That(first.EndTimestamp).IsEqualTo(new DateTimeOffset(2026, 3, 15, 10, 0, 0, TimeSpan.Zero));
        await Assert.That(first.CompleteLines).IsEqualTo(102);
        await Assert.That(ParentLines().Max()).IsEqualTo(101);

        _server.ResetLogEntries();
        var second = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, parent, "s1", new SessionMetadata(), encodedCwd: null, TimeProvider.System,
            startLine: first.CompleteLines, strict: true);
        await Assert.That(second.AgentIds).IsEquivalentTo([Early]);
        await Assert.That(ParentLines()).IsEquivalentTo([102, 103]);
        await Assert.That(second.TailDeferred).IsFalse();
        await Assert.That(second.EndTimestamp).IsEqualTo(new DateTimeOffset(2026, 3, 15, 11, 0, 0, TimeSpan.Zero));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Missing_child_transcript_leaves_its_parent_reference_retryable(bool disappearsAfterStart) {
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");
        var parent = await WriteSessionAsync();
        var directory = Path.Combine(Path.ChangeExtension(parent, null), "subagents");
        var child = Path.Combine(directory, $"agent-{Late}.jsonl");
        if (!disappearsAfterStart) File.Delete(child);

        using var client = new HttpClient();
        await Assert.ThrowsAsync<IOException>(async () => await SessionImporter.ImportSessionAsync(
            client, _server.Url!, parent, "s1", new SessionMetadata(), encodedCwd: null, TimeProvider.System,
            new OnProgress(progress => {
                if (progress is SubagentStarted { AgentId: Late }) File.Delete(child);
            }), startLine: 2, strict: true));

        await Assert.That(ParentLines()).IsEquivalentTo(disappearsAfterStart ? [2] : Array.Empty<int>());
        await Assert.That(Bodies("/hooks/subagent-stop")).IsEmpty();

        _server.ResetLogEntries();
        await File.WriteAllTextAsync(child, "");
        var retry = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, parent, "s1", new SessionMetadata(), encodedCwd: null,
            TimeProvider.System, startLine: disappearsAfterStart ? 3 : 2, strict: true);
        await Assert.That(retry.AgentIds).IsEquivalentTo([Late]);
        await Assert.That(ParentLines().Max()).IsEqualTo(4);
    }

    sealed class OnProgress(Action<ImportProgress> action) : IProgress<ImportProgress> {
        public void Report(ImportProgress progress) => action(progress);
    }

    [Test]
    public async Task New_import_delivers_trailing_agents_after_the_parent() {
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");

        var path = Tmp.CreateFile("new.jsonl", """{"type":"user","message":{"content":"parent"}}""" + "\n");
        Tmp.CreateFile(["new", "subagents", $"agent-{Compact}.jsonl"], """{"type":"user","message":{"content":"child"}}""" + "\n");

        using var client = new HttpClient();
        var result = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, path, "s1", new SessionMetadata(), encodedCwd: null, TimeProvider.System);

        await Assert.That(result.AgentIds).IsEquivalentTo([Compact]);
        var posts = _server.LogEntries.Select(e => e.RequestMessage.Path).ToList();
        await Assert.That(string.Join(" ", posts)).IsEqualTo("/hooks/transcript /hooks/subagent-start /hooks/transcript /hooks/subagent-stop");
    }
}
