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
}
