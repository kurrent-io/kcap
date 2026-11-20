using System.Net;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Transcripts;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit.Transcripts;

public class TranscriptPostTests {
    const string Session = "5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a";
    const string Base    = "http://server";
    const string Raw     = "/hooks/transcript";

    const string Unsupported = """{"error":"canonical_unsupported","reason":"disabled"}""";

    static readonly DateTimeOffset Received = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    static string Id(int n) => $"10000000-0000-4000-8000-{n:D12}";

    static string User(int n) =>
        $$$"""{"type":"user","uuid":"{{{Id(n)}}}","parentUuid":null,"timestamp":"2026-11-16T08:00:{{{n:D2}}}Z","message":{"role":"user","content":"line {{{n}}}"}}""";

    static TranscriptBatch Batch(int first, int count) =>
        new() { SessionId = Session, Lines = [.. Enumerable.Range(first, count).Select(User)], LineNumbers = [.. Enumerable.Range(first, count)] };

    static CanonicalRoute Route() => CanonicalRoute.For("claude", Session, null, true)!;

    static string RawJson(TranscriptBatch batch) => JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.TranscriptBatch);

    static string CanonicalJson(CanonicalTranscriptBatch batch) => JsonSerializer.Serialize(batch, CapacitorJsonContext.Default.CanonicalTranscriptBatch);

    [Test]
    public async Task A_stored_canonical_batch_is_acknowledged() {
        using var server = new Server((_, _) => Reply(HttpStatusCode.OK));
        var route   = Route();
        var batch   = Batch(0, 2);
        var encoded = route.Encode(batch, Received);

        using var response = await Send(server, batch, route);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.Paths).IsEquivalentTo([CanonicalRoute.HttpPath]);
        await Assert.That(server.Bodies[0]).IsEqualTo(CanonicalJson(encoded));
        // Acknowledged, the route no longer holds the batch for a resend.
        await Assert.That(route.Encode(batch, Received)).IsNotSameReferenceAs(encoded);
    }

    /// <summary>A refusal a retry would not change is a verdict on the canonical encoding, not on the lines, which the
    /// raw route may still store: one the server cannot take, a body it cannot bind or one over its size limit.</summary>
    [Test]
    [Arguments(HttpStatusCode.UnprocessableEntity, Unsupported)]
    [Arguments(HttpStatusCode.UnprocessableEntity, """{"error":"invalid_batch"}""")]
    [Arguments(HttpStatusCode.BadRequest, "")]
    [Arguments(HttpStatusCode.RequestEntityTooLarge, "")]
    public async Task A_refusal_a_retry_would_not_change_falls_the_route_back_and_sends_the_same_batch_raw(HttpStatusCode status, string body) {
        using var server = new Server((path, _) => path == CanonicalRoute.HttpPath ? Reply(status, body) : Reply(HttpStatusCode.OK));
        var route = Route();
        var batch = Batch(0, 2);

        using var response = await Send(server, batch, route);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        await Assert.That(server.Paths).IsEquivalentTo([CanonicalRoute.HttpPath, Raw], CollectionOrdering.Matching);
        await Assert.That(server.Bodies[1]).IsEqualTo(RawJson(batch));
        await Assert.That(route.Active).IsFalse();
    }

    /// <summary>The raw route's answer is the verdict on the lines, refusal included.</summary>
    [Test]
    public async Task The_raw_answer_to_a_batch_refused_canonical_is_returned() {
        using var server = new Server((_, _) => Reply(HttpStatusCode.BadRequest));

        using var response = await Send(server, Batch(0, 2), Route());

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.BadRequest);
        await Assert.That(server.Paths).IsEquivalentTo([CanonicalRoute.HttpPath, Raw], CollectionOrdering.Matching);
    }

    /// <summary>A retry can still store a batch answered with a lapsed credential, a timeout, a throttle or a server
    /// fault: the producer spools or retries it, and the batch stays encoded for its resend.</summary>
    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.RequestTimeout)]
    [Arguments(HttpStatusCode.TooManyRequests)]
    [Arguments(HttpStatusCode.InternalServerError)]
    [Arguments(HttpStatusCode.ServiceUnavailable)]
    public async Task A_retryable_refusal_is_returned_as_it_came(HttpStatusCode status) {
        using var server = new Server((_, _) => Reply(status));
        var route   = Route();
        var batch   = Batch(0, 2);
        var encoded = route.Encode(batch, Received);

        using var response = await Send(server, batch, route);

        await Assert.That(response.StatusCode).IsEqualTo(status);
        await Assert.That(server.Paths).IsEquivalentTo([CanonicalRoute.HttpPath]);
        await Assert.That(route.Active).IsTrue();
        await Assert.That(route.Encode(batch, Received)).IsSameReferenceAs(encoded);
    }

    [Test]
    public async Task Without_a_route_the_batch_goes_raw() {
        using var server = new Server((_, _) => Reply(HttpStatusCode.OK));
        var batch = Batch(0, 2);

        using var response = await Send(server, batch, route: null);

        await Assert.That(server.Paths).IsEquivalentTo([Raw]);
        await Assert.That(server.Bodies[0]).IsEqualTo(RawJson(batch));
    }

    [Test]
    public async Task A_batch_that_cannot_be_projected_goes_raw() {
        var deep  = string.Concat(Enumerable.Repeat("[", 58)) + string.Concat(Enumerable.Repeat("]", 58));
        var line  = $$$$"""{"type":"assistant","uuid":"{{{{Id(2)}}}}","timestamp":"2026-11-16T08:00:02Z","message":{"model":"claude-x","content":[{"type":"tool_use","id":"t1","name":"X","input":{"a":{{{{deep}}}}}}]}}""";
        var batch = new TranscriptBatch { SessionId = Session, Lines = [line], LineNumbers = [0] };
        var route = Route();
        using var server = new Server((_, _) => Reply(HttpStatusCode.OK));

        using var response = await Send(server, batch, route);

        await Assert.That(server.Paths).IsEquivalentTo([Raw]);
        await Assert.That(route.Active).IsFalse();
    }

    /// <summary>The chunks of one read share its commits, and every chunk but the last says more follows: a chunk
    /// resent raw must still say both, or the server stamps a commit the next chunk owns.</summary>
    [Test]
    public async Task A_refused_chunk_and_the_rest_of_a_split_read_go_raw_with_their_commits() {
        var canonicalPosts = 0;
        using var server = new Server((path, _) => path == CanonicalRoute.HttpPath && ++canonicalPosts == 2
            ? Reply(HttpStatusCode.UnprocessableEntity, Unsupported)
            : Reply(HttpStatusCode.OK));
        var commits = new[] { new ObservedCommit { Sha = "aaa", Message = "Fix watcher" } };
        var chunks  = new[] {
            Batch(0, 2) with { ObservedCommits = commits, MoreFollows = true },
            Batch(2, 2) with { ObservedCommits = commits, MoreFollows = true },
            Batch(4, 2) with { ObservedCommits = commits },
        };
        var route = Route();

        foreach (var chunk in chunks) {
            using var _ = await Send(server, chunk, route);
        }

        await Assert.That(server.Paths).IsEquivalentTo([CanonicalRoute.HttpPath, CanonicalRoute.HttpPath, Raw, Raw]);
        await Assert.That(server.Bodies[2]).IsEqualTo(RawJson(chunks[1]));
        await Assert.That(server.Bodies[3]).IsEqualTo(RawJson(chunks[2]));
    }

    static async Task<HttpResponseMessage> Send(Server server, TranscriptBatch batch, CanonicalRoute? route) {
        using var client = new HttpClient(server, disposeHandler: false);

        return await TranscriptPost.SendAsync(client, Base, batch, route, TimeProvider.System, mayAttempt: null, CancellationToken.None);
    }

    static HttpResponseMessage Reply(HttpStatusCode status, string body = "") =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    /// Records every request's path and body, in order, and answers each from <c>reply</c>.
    sealed class Server(Func<string, string, HttpResponseMessage> reply) : HttpMessageHandler {
        public List<string> Paths  { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var path = request.RequestUri!.AbsolutePath;
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);

            Paths.Add(path);
            Bodies.Add(body);

            return reply(path, body);
        }
    }
}
