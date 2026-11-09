using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Capture;
using Capacitor.Cli.Core;
using Capacitor.Cli.Tests.Unit.Capture;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit;

public class PostWithRetryTests : IDisposable {
    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task SucceedsOnFirstAttempt() {
        _server.Given(Request.Create().WithPath("/hooks/session-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("ok"));

        using var client   = new HttpClient();
        using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
        var       response = await client.PostWithRetryAsync($"{_server.Url}/hooks/session-start", content, TimeProvider.System);

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
    }

    [Test]
    public async Task RetriesOnConnectionFailureThenSucceeds() {
        // The retry path fires on a transport fault, so inject one: a first attempt that faults like
        // a refused connection, then a success. Driving this through a real server on a restarted
        // port is racy — a neighbour can grab the freed port, or a request can land after the
        // listener opens but before its mapping is registered, which returns a final 404.
        var handler = new ScriptedHandler(
            _ => throw new HttpRequestException("connection refused"),
            _ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("recovered") });

        using var client   = new HttpClient(handler);
        using var content  = new StringContent("{}", Encoding.UTF8, "application/json");
        var       response = await client.PostWithRetryAsync(
            "http://localhost/hooks/test", content, TimeProvider.System, timeout: TimeSpan.FromSeconds(15));

        await Assert.That((int)response.StatusCode).IsEqualTo(200);
        await Assert.That(await response.Content.ReadAsStringAsync()).IsEqualTo("recovered");
    }

    sealed class ScriptedHandler(params Func<HttpRequestMessage, HttpResponseMessage>[] steps) : HttpMessageHandler {
        int _attempt;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var step = steps[Math.Min(_attempt++, steps.Length - 1)];
            try                  { return Task.FromResult(step(request)); }
            catch (Exception ex) { return Task.FromException<HttpResponseMessage>(ex); }
        }
    }
}

public class InlineDrainTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherManager? _watchers;
    WatcherManager  Watchers => _watchers ??= TestWatchers.For(Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient());

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task PostsCorrectBatch() {
        const string sessionId = "test-session-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var tmp      = new TempDir();
        var transcriptPath = tmp.PathTo("transcript.jsonl");

        await File.WriteAllTextAsync(
            transcriptPath,
            """{"type":"user","message":{"content":"hello"}}"""        + "\n" +
            """{"type":"assistant","message":{"content":"hi back"}}""" + "\n"
        );

        await Watchers.InlineDrainAsync(sessionId, transcriptPath, null);

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("session_id").GetString()).IsEqualTo(sessionId);
        await Assert.That(root.GetProperty("lines").GetArrayLength()).IsEqualTo(2);
        await Assert.That(root.GetProperty("line_numbers")[0].GetInt32()).IsEqualTo(0);
        await Assert.That(root.GetProperty("line_numbers")[1].GetInt32()).IsEqualTo(1);
    }

    [Test]
    public async Task PostsCorrectBatch_with_codex_vendor_when_specified() {
        const string sessionId = "test-session-codex-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var tmp      = new TempDir();
        var transcriptPath = tmp.PathTo("rollout.jsonl");

        await File.WriteAllTextAsync(
            transcriptPath,
            """{"timestamp":"2026-05-07T15:50:21.989Z","type":"session_meta","payload":{}}""" + "\n"
        );

        await Watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null, vendor: "codex");

        var requests = _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost());

        await Assert.That(requests.Count).IsEqualTo(1);

        var root = JsonDocument.Parse(requests[0].RequestMessage.Body!).RootElement;
        await Assert.That(root.GetProperty("vendor").GetString()).IsEqualTo("codex");
    }

    const string Line = """{"type":"user","message":{"content":"hello"}}""";

    // The first try of a line has started and has yet to end.
    const int MidFirstTry = 3;

    static readonly TimeSpan PastARecordBudget = TimeSpan.FromSeconds(1.2);

    /// <summary>The watcher is gone by now, so a line a stall held would wait for a manual import, and every line after it.</summary>
    [Test]
    public async Task A_stall_during_redaction_does_not_end_the_drain_at_that_line() {
        var clock = new StallingRedactionClock();
        clock.StallOnReading(MidFirstTry, PastARecordBudget);

        await Assert.That(LineNumbers(await PostsUnder(clock))).IsEqualTo("0,1,2");
    }

    /// <summary>A caller's cap can abandon the second try, and a drain it abandons posts nothing further.</summary>
    [Test]
    public async Task The_lines_before_a_held_line_go_out_ahead_of_its_second_try() {
        var clock = new StallingRedactionClock();
        clock.StallOnReading(ReadingsOfALine() + MidFirstTry, PastARecordBudget);

        await Assert.That(LineNumbers(await PostsUnder(clock))).IsEqualTo("0 | 1,2");
    }

    [Test]
    public async Task A_line_held_twice_leaves_only_the_lines_before_it_sent() {
        var clock = new StallingRedactionClock();
        clock.StallFromReading(ReadingsOfALine() + MidFirstTry, PastARecordBudget);

        await Assert.That(LineNumbers(await PostsUnder(clock))).IsEqualTo("0");
    }

    /// <summary>Sent marked, or skipped, the line is one the server drops when an import brings it later.</summary>
    [Test]
    public async Task A_line_that_stays_unredacted_stops_the_drain_with_nothing_sent_for_it() {
        await Assert.That(LineNumbers(await PostsUnder(new SwitchableRedactionClock()))).IsEqualTo("");
    }

    /// <summary>A post that left them out would tell the server to read commits from its lines instead.</summary>
    [Test]
    public async Task A_drain_of_two_posts_sends_its_commits_once_and_reports_on_them_in_both() {
        using var tmp   = new TempDir();
        var       filed = new SessionCommits(tmp.PathTo("commits.jsonl"));
        filed.Append(new ObservedCommit { Sha = "aaa", Message = "Fix watcher" });

        var clock = new StallingRedactionClock();
        clock.StallOnReading(ReadingsOfALine() + MidFirstTry, PastARecordBudget);

        var posts = await PostsUnder(clock, new CommitObservation.Covered(filed));

        await Assert.That(posts.Count).IsEqualTo(2);
        await Assert.That(posts[0].GetProperty("observed_commits").GetArrayLength()).IsEqualTo(1);
        await Assert.That(posts[1].GetProperty("observed_commits").GetArrayLength()).IsEqualTo(0);
    }

    static int ReadingsOfALine() {
        var clock = new StallingRedactionClock();
        new InlineDrainRedaction(clock, InlineDrainRedactionTests.Unbounded).Encode(Line, static (_, _) => { });

        return clock.Readings;
    }

    static string LineNumbers(List<JsonElement> posts) =>
        string.Join(" | ", posts.Select(post => string.Join(',', post.GetProperty("line_numbers").EnumerateArray().Select(number => number.GetInt32()))));

    async Task<List<JsonElement>> PostsUnder(TimeProvider redactionClock, CommitObservation? commits = null) {
        const string sessionId = "test-session-stalled-drain";

        _server.Given(Request.Create().WithPath($"/api/sessions/{sessionId}/last-line").UsingGet())
            .RespondWith(
                Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/json")
                    .WithBody("""{"last_line_number": -1}""")
            );

        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        using var tmp      = new TempDir();
        var transcriptPath = tmp.PathTo("transcript.jsonl");

        await File.WriteAllTextAsync(transcriptPath, string.Concat(Enumerable.Repeat(Line + "\n", 3)));

        var watchers = TestWatchers.For(
            Config.Root, Resolutions.At(_server.Url!, Config.Root), new FixedCapacitorHttpClient(),
            () => new InlineDrainRedaction(redactionClock, InlineDrainRedactionTests.Unbounded), commits);
        await watchers.InlineDrainAsync(sessionId, transcriptPath, agentId: null);

        return _server.FindLogEntries(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .Select(request => JsonDocument.Parse(request.RequestMessage.Body!).RootElement)
            .ToList();
    }
}

public class SessionStartAdditionalContextTests {
    [Test]
    public async Task BuildEnvelope_returns_null_when_no_fragments() {
        var result = SessionStartAdditionalContext.BuildEnvelope();
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_returns_null_when_all_fragments_null() {
        var result = SessionStartAdditionalContext.BuildEnvelope(null, null, null);
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_returns_null_when_all_fragments_empty_or_whitespace() {
        var result = SessionStartAdditionalContext.BuildEnvelope("", "   ", "\n\t");
        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_wraps_single_fragment_in_envelope() {
        var result = SessionStartAdditionalContext.BuildEnvelope("hello world");

        await Assert.That(result).IsNotNull();
        var json = JsonNode.Parse(result!);
        await Assert.That(json!["hookSpecificOutput"]!["hookEventName"]!.GetValue<string>()).IsEqualTo("SessionStart");
        await Assert.That(json["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>()).IsEqualTo("hello world");
    }

    [Test]
    public async Task BuildEnvelope_joins_multiple_fragments_with_blank_line_in_order() {
        var result = SessionStartAdditionalContext.BuildEnvelope("first", "second");

        await Assert.That(result).IsNotNull();
        var ctx = JsonNode.Parse(result!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).IsEqualTo("first\n\nsecond");
    }

    [Test]
    public async Task BuildEnvelope_skips_null_and_blank_when_mixed_with_real_fragments() {
        var result = SessionStartAdditionalContext.BuildEnvelope(null, "first", "   ", "second", null);

        await Assert.That(result).IsNotNull();
        var ctx = JsonNode.Parse(result!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();
        await Assert.That(ctx).IsEqualTo("first\n\nsecond");
    }

    static string Context(string envelope) =>
        JsonNode.Parse(envelope)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>();

    [Test]
    public async Task BuildEnvelope_drops_a_fragment_that_does_not_fit_whole_and_keeps_later_ones() {
        var big = new string('x', SessionStartAdditionalContext.MaxContextChars);

        var ctx = Context(SessionStartAdditionalContext.BuildEnvelope("first", big, "last")!);

        await Assert.That(ctx).IsEqualTo("first\n\nlast");
    }

    /// <summary>The cut is reported by name, and the fragment that fitted is not.</summary>
    [Test]
    public async Task BuildEnvelope_cuts_a_trimmable_fragment_at_a_line_break_to_fit_the_cap() {
        var line = new string('m', 99);
        var list = string.Join('\n', Enumerable.Repeat(line, 200));
        var said = new List<string>();

        var ctx = Context(SessionStartAdditionalContext.BuildRankedEnvelope(
            [new("nudge", Name: "nudge"), new(list, Trimmable: true, Name: "memory")], said.Add)!);

        await Assert.That(ctx.Length).IsLessThanOrEqualTo(SessionStartAdditionalContext.MaxContextChars);
        await Assert.That(ctx).StartsWith("nudge\n\n" + line);
        await Assert.That(ctx).EndsWith(line);
        await Assert.That(said).IsEquivalentTo(["session-start memory cut: over Claude Code's 10000-character context cap"]);
    }

    [Test]
    public async Task BuildEnvelope_funds_a_low_rank_fragment_before_a_long_one_placed_ahead_of_it() {
        var list = string.Join('\n', Enumerable.Repeat(new string('g', 99), 200));

        var ctx = Context(SessionStartAdditionalContext.BuildRankedEnvelope([new(list, Rank: 2, Trimmable: true), new("## Plans")])!);

        await Assert.That(ctx.Length).IsLessThanOrEqualTo(SessionStartAdditionalContext.MaxContextChars);
        await Assert.That(ctx).StartsWith("ggg");
        await Assert.That(ctx).EndsWith("\n\n## Plans");
    }

    [Test]
    public async Task BuildEnvelope_drops_an_untrimmable_fragment_that_does_not_fit_whole() {
        var block = "<data>\n" + string.Join('\n', Enumerable.Repeat(new string('d', 99), 200)) + "\n</data>";
        var said  = new List<string>();

        var ctx = Context(SessionStartAdditionalContext.BuildRankedEnvelope([new("nudge"), new(block, Rank: 1, Name: "next work")], said.Add)!);

        await Assert.That(ctx).IsEqualTo("nudge");
        await Assert.That(said).IsEquivalentTo(["session-start next work dropped: over Claude Code's 10000-character context cap"]);
    }

    [Test]
    public async Task BuildEnvelope_drops_a_trimmable_fragment_with_no_line_break_inside_the_room_left() {
        var result = SessionStartAdditionalContext.BuildRankedEnvelope(
            [new(new string('m', SessionStartAdditionalContext.MaxContextChars + 1), Trimmable: true)]);

        await Assert.That(result).IsNull();
    }

    [Test]
    public async Task BuildEnvelope_produces_single_top_level_json_object() {
        var result = SessionStartAdditionalContext.BuildEnvelope("first", "second")!;
        // Guard against a future bug that appends a second envelope: nothing
        // (not even whitespace) should follow the last closing brace.
        var lastClose  = result.LastIndexOf('}');
        var afterClose = result[(lastClose + 1)..].Trim();
        await Assert.That(afterClose).IsEqualTo("");
    }
}
