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

        foreach (var agent in new[] { Early, Late })
            await File.WriteAllLinesAsync(agentsDir.PathTo($"agent-{agent}.jsonl"), [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"a"}}""",
            ]);

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
    public async Task Strict_resume_stops_before_the_parent_lines_after_a_refused_subagent() {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost().WithBody(new JsonPartialMatcher($$"""{"agent_id":"{{Late}}"}""")))
            .AtPriority(1)
            .RespondWith(Response.Create().WithStatusCode(400));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .AtPriority(2)
            .RespondWith(Response.Create().WithStatusCode(200));
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");
        var sessionPath = await WriteSessionAsync();

        using var client = new HttpClient();
        await Assert.ThrowsAsync<HttpRequestException>(async () =>
            await SessionImporter.ImportSessionAsync(
                client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
                TimeProvider.System, startLine: 2, strict: true));

        var parentLines = Bodies("/hooks/transcript")
            .Where(b => b["agent_id"] is null)
            .SelectMany(b => b["line_numbers"]!.AsArray().Select(n => n!.GetValue<int>()))
            .ToList();
        await Assert.That(parentLines).IsEquivalentTo([2]);
        await Assert.That(Bodies("/hooks/subagent-stop")).IsEmpty();
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
    public async Task A_refused_trailing_subagent_holds_the_last_parent_batch_until_a_retry_delivers_it() {
        var name        = Guid.NewGuid().ToString("N");
        var sessionPath = Tmp.PathTo($"{name}.jsonl");
        var agentsDir   = Tmp.CreateDir(name, "subagents");
        await File.WriteAllLinesAsync(sessionPath, Enumerable.Range(0, 150).Select(i =>
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
        await Assert.That(ParentLines().Max()).IsEqualTo(149);
    }

    /// <summary>
    /// A subagent first referenced before the watermark but still reporting progress after it was
    /// running at the earlier import; one whose references all precede the watermark had finished.
    /// </summary>
    [Test]
    public async Task Resume_resends_a_subagent_still_running_at_the_watermark_at_its_next_reference() {
        RespondOk("/hooks/transcript");
        RespondOk("/hooks/subagent-start");
        RespondOk("/hooks/subagent-stop");

        var name        = Guid.NewGuid().ToString("N");
        var sessionPath = Tmp.PathTo($"{name}.jsonl");
        var agentsDir   = Tmp.CreateDir(name, "subagents");
        await File.WriteAllLinesAsync(sessionPath, [
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Early}}}"}}""",
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Late}}}"}}""",
            """{"type":"user","timestamp":"2026-03-15T10:00:01Z","message":{"content":"p2"}}""",
            $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{Late}}}"}}""",
        ]);
        foreach (var agent in new[] { Early, Late })
            await File.WriteAllLinesAsync(agentsDir.PathTo($"agent-{agent}.jsonl"), [
                """{"type":"user","timestamp":"2026-03-15T10:00:00Z","message":{"content":"a"}}""",
            ]);

        using var client = new HttpClient();
        var result = await SessionImporter.ImportSessionAsync(
            client, _server.Url!, sessionPath, "s1", new SessionMetadata { Cwd = "/x" }, encodedCwd: null,
            TimeProvider.System, startLine: 2, strict: true);

        await Assert.That(result.AgentIds).IsEquivalentTo([Late]);
        await Assert.That(ParentLines()).IsEquivalentTo([2, 3]);
    }

    [Test]
    public async Task ResumePositions_take_the_first_reference_past_the_watermark() {
        var positions = SessionImporter.ResumePositions(
            new() { ["done"] = 1, ["running"] = 1, ["new"] = 5 },
            new() { ["done"] = [1, 2], ["running"] = [1, 4, 7], ["new"] = [5] },
            startLine: 3);

        await Assert.That(positions).IsEquivalentTo(new Dictionary<string, int> { ["running"] = 4, ["new"] = 5 });
    }
}
