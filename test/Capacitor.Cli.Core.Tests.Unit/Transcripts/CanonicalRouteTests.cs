using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Core.Transcripts;

namespace Capacitor.Cli.Core.Tests.Unit.Transcripts;

public class CanonicalRouteTests {
    const string Session = "5cdc4fcc-ff9e-4fdc-8d93-cf47ff5a140a";

    static readonly DateTimeOffset Received = new(2026, 11, 16, 9, 0, 0, TimeSpan.Zero);

    [TempDir] public required TempDir Tmp { get; init; }

    static string Id(int n) => $"10000000-0000-4000-8000-{n:D12}";

    static string User(int n, int? parent = null) =>
        $$$"""{"type":"user","uuid":"{{{Id(n)}}}","parentUuid":{{{Parent(parent)}}},"timestamp":"2026-11-16T08:00:{{{n:D2}}}Z","message":{"role":"user","content":"line {{{n}}}"}}""";

    static string Parent(int? parent) => parent is { } p ? $"\"{Id(p)}\"" : "null";

    static string Assistant(int n, int parent) =>
        $$$$"""{"type":"assistant","uuid":"{{{{Id(n)}}}}","parentUuid":"{{{{Id(parent)}}}}","timestamp":"2026-11-16T08:00:{{{{n:D2}}}}Z","message":{"model":"claude-x","content":[{"type":"text","text":"reply"}],"usage":{"input_tokens":10,"output_tokens":5}}}""";

    static TranscriptBatch Raw(int first, params string[] lines) =>
        new() { SessionId = Session, Lines = lines, LineNumbers = [.. Enumerable.Range(first, lines.Length)] };

    static CanonicalRoute Route() => CanonicalRoute.For("claude", Session, null, true)!;

    static string[] Events(CanonicalTranscriptBatch batch) =>
        [.. batch.Events.Select(e => JsonSerializer.Serialize(e, CapacitorJsonContext.Default.CanonicalBatchEvent))];

    [Test]
    public async Task Only_a_supported_canonical_vendor_gets_a_route() {
        await Assert.That(CanonicalRoute.For("codex", Session, null, true)).IsNull();
        await Assert.That(CanonicalRoute.For("claude", Session, null, false)).IsNull();
        await Assert.That(CanonicalRoute.For("claude", Session, null, true)).IsNotNull();
        await Assert.That(CanonicalRoute.For(null, Session, null, true)).IsNotNull();
    }

    /// <summary>The server files a stream under its canonical ids, and derives an image's id from the session's alone,
    /// on a subagent's stream too: a batch carrying any other id is refused.</summary>
    [Test]
    [Arguments(null, null)]
    [Arguments("a4f2c1e9b7d3", "a4f2c1e9b7d3")]
    [Arguments("0b0c0d0e-1111-4222-8333-444455556666", "0b0c0d0e111142228333444455556666")]
    public async Task A_route_sends_the_servers_ids_for_a_dashed_session(string? agentId, string? canonicalAgentId) {
        const string image = """{"type":"user","uuid":"10000000-0000-4000-8000-000000000001","timestamp":"2026-11-16T08:00:01Z","message":{"content":[{"type":"text","text":"look"},{"type":"image","source":{"type":"base64","media_type":"image/png","data":"iVBORw0KGgo="}}]}}""";

        var batch = CanonicalRoute.For("claude", Session, agentId, true)!.Encode(Raw(0, image) with { AgentId = agentId }, Received);

        await Assert.That(batch.SessionId).IsEqualTo("5cdc4fccff9e4fdc8d93cf47ff5a140a");
        await Assert.That(batch.AgentId).IsEqualTo(canonicalAgentId);
        var message = batch.Events.Single();
        await Assert.That(message.Attachments!.Single().Id)
            .IsEqualTo(ServerAttachmentId("5cdc4fccff9e4fdc8d93cf47ff5a140a", message.EventId, 0));
    }

    /// The server's derivation: the session's UTF-8, the message id's bytes, the image's index little-endian.
    static Guid ServerAttachmentId(string canonicalSession, Guid messageId, int index) {
        var session = Encoding.UTF8.GetBytes(canonicalSession);
        var input   = new byte[session.Length + 20];
        session.CopyTo(input, 0);
        messageId.TryWriteBytes(input.AsSpan(session.Length));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(session.Length + 16), index);

        return new Guid(XxHash128.Hash(input));
    }

    [Test]
    public async Task Replay_of_a_missing_file_says_it_lacked_lines() {
        var route = Route();

        await Assert.That(await route.ReplayAsync(Tmp.PathTo("none.jsonl"), 5, CancellationToken.None)).IsFalse();
        await Assert.That(route.Active).IsTrue();
    }

    [Test]
    public async Task Replay_of_a_file_shorter_than_the_bound_says_it_lacked_lines() {
        var path  = Tmp.CreateFile("short.jsonl", $"{User(1)}\n");
        var route = Route();

        await Assert.That(await route.ReplayAsync(path, 10, CancellationToken.None)).IsFalse();
        await Assert.That(route.Active).IsTrue();
    }

    [Test]
    public async Task Replay_to_no_line_needs_no_file() {
        await Assert.That(await Route().ReplayAsync(Tmp.PathTo("none.jsonl"), 0, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task Replay_leaves_the_batches_after_it_as_an_uninterrupted_run_has_them() {
        var lines = new[] { User(1), Assistant(2, 1), User(3, 2) };

        var whole = Route();
        whole.Encode(Raw(0, lines[0], lines[1]), Received);
        whole.Acknowledged();
        var expected = Events(whole.Encode(Raw(2, lines[2]), Received));

        var path     = Tmp.CreateFile("t.jsonl", string.Join('\n', lines) + "\n");
        var replayed = Route();

        await Assert.That(await replayed.ReplayAsync(path, 2, CancellationToken.None)).IsTrue();
        await Assert.That(Events(replayed.Encode(Raw(2, lines[2]), Received))).IsEquivalentTo(expected);
    }

    static string AssistantUsing(int n, int parent, int inputTokens, string text) =>
        $$$$"""{"type":"assistant","uuid":"{{{{Id(n)}}}}","parentUuid":"{{{{Id(parent)}}}}","timestamp":"2026-11-16T08:00:{{{{n:D2}}}}Z","message":{"model":"claude-x","content":[{"type":"text","text":"{{{{text}}}}"}],"usage":{"input_tokens":{{{{inputTokens}}}},"output_tokens":1}}}""";

    /// What the capture sends in place of a line over the record limit.
    const string CaptureLoss = $$"""{"type":"{{CaptureLossLine.Type}}","reason":"input_limit"}""";

    /// <summary>The oversized record went out as a capture-loss line, which projects nothing. Projected from the raw file,
    /// its usage would make the next record's, the same usage, an echo that stores nothing.</summary>
    [Test]
    public async Task Replay_skips_a_line_its_capture_replaced_as_the_route_that_sent_it_did() {
        var oversized = AssistantUsing(2, parent: 1, inputTokens: 7, new string('x', CaptureRule.MaxRecordBytes));
        var next      = AssistantUsing(3, parent: 2, inputTokens: 7, "short");

        var live = Route();
        live.Encode(Raw(0, User(1), CaptureLoss), Received);
        live.Acknowledged();
        var sent = live.Encode(Raw(2, next), Received);

        var path     = Tmp.CreateFile("t.jsonl", $"{User(1)}\n{oversized}\n{next}\n");
        var replayed = Route();

        await Assert.That(await replayed.ReplayAsync(path, 2, CancellationToken.None)).IsTrue();
        await Assert.That(sent.Events.Single().Usage).IsNotNull();
        await Assert.That(Events(replayed.Encode(Raw(2, next), Received))).IsEquivalentTo(Events(sent));
    }

    /// A readable line a rewrite could take past the record limit, carrying a usage of 7 input tokens.
    static string Large(int n, int parent) =>
        AssistantUsing(n, parent, inputTokens: 7, new string('x', CaptureRule.MaxRecordBytes / CaptureRule.MaxRewriteGrowth));

    /// <summary>Whether a rewrite took this line past the record limit depends on the secrets in it, so without the
    /// producer's redaction the lines after it cannot be projected as the capture sent them.</summary>
    [Test]
    public async Task Without_the_producers_redaction_a_replay_stops_at_a_large_line() {
        var path  = Tmp.CreateFile("t.jsonl", $"{User(1)}\n{Large(2, 1)}\n{User(3, 2)}\n");
        var route = Route();

        await Assert.That(await route.ReplayAsync(path, 3, CancellationToken.None)).IsFalse();
        await Assert.That(route.Active).IsTrue();
    }

    /// <summary>The capture sent the large line, so the line after it, with the same usage, is an echo.</summary>
    [Test]
    public async Task A_large_line_the_producers_redaction_keeps_is_replayed_as_it_was_sent() {
        var (large, next) = (Large(2, 1), AssistantUsing(3, parent: 2, inputTokens: 7, "short"));
        var asked         = new List<(string Line, CancellationToken Ct)>();
        using var replay  = new CancellationTokenSource();

        var live = Route();
        live.Encode(Raw(0, User(1), large), Received);
        live.Acknowledged();
        var sent = live.Encode(Raw(2, next), Received);

        var path     = Tmp.CreateFile("t.jsonl", $"{User(1)}\n{large}\n{next}\n");
        var replayed = CanonicalRoute.For("claude", Session, null, true, (line, ct) => {
            asked.Add((line, ct));

            return LineCapture.Kept;
        })!;

        await Assert.That(await replayed.ReplayAsync(path, 2, replay.Token)).IsTrue();
        await Assert.That(sent.Events.Single().Usage).IsNull();
        await Assert.That(Events(replayed.Encode(Raw(2, next), Received))).IsEquivalentTo(Events(sent));
        await Assert.That(asked).IsEquivalentTo([(large, replay.Token)]);
    }

    /// <summary>The capture sent a capture-loss line, so the line after it carries its usage.</summary>
    [Test]
    public async Task A_large_line_the_producers_redaction_replaces_is_skipped() {
        var next = AssistantUsing(3, parent: 2, inputTokens: 7, "short");

        var live = Route();
        live.Encode(Raw(0, User(1), CaptureLoss), Received);
        live.Acknowledged();
        var sent = live.Encode(Raw(2, next), Received);

        var path     = Tmp.CreateFile("t.jsonl", $"{User(1)}\n{Large(2, 1)}\n{next}\n");
        var replayed = CanonicalRoute.For("claude", Session, null, true, (_, _) => LineCapture.Lost)!;

        await Assert.That(await replayed.ReplayAsync(path, 2, CancellationToken.None)).IsTrue();
        await Assert.That(sent.Events.Single().Usage).IsNotNull();
        await Assert.That(Events(replayed.Encode(Raw(2, next), Received))).IsEquivalentTo(Events(sent));
    }

    /// <summary>A replay the caller stops while a large line is being redacted is stopped, as between lines, not
    /// incomplete.</summary>
    [Test]
    public async Task A_replay_stopped_while_a_large_line_is_redacted_is_cancelled() {
        var       path  = Tmp.CreateFile("t.jsonl", $"{User(1)}\n{Large(2, 1)}\n{User(3, 2)}\n");
        using var stop  = new CancellationTokenSource();
        var       route = CanonicalRoute.For("claude", Session, null, true, (_, _) => {
            stop.Cancel();

            return LineCapture.Undecided;
        })!;

        await Assert.That(() => route.ReplayAsync(path, 3, stop.Token)).Throws<OperationCanceledException>();
    }

    [Test]
    public async Task A_large_line_the_producers_redaction_cannot_decide_ends_the_replay() {
        var path  = Tmp.CreateFile("t.jsonl", $"{User(1)}\n{Large(2, 1)}\n{User(3, 2)}\n");
        var route = CanonicalRoute.For("claude", Session, null, true, (_, _) => LineCapture.Undecided)!;

        await Assert.That(await route.ReplayAsync(path, 3, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task Encoding_the_same_batch_again_returns_it_and_the_next_batch_is_unaffected() {
        var (first, second) = (Raw(0, User(1), Assistant(2, 1)), Raw(2, User(3, 2)));

        var uninterrupted = Route();
        uninterrupted.Encode(first, Received);
        uninterrupted.Acknowledged();
        var expected = Events(uninterrupted.Encode(second, Received));

        var route  = Route();
        var once   = route.Encode(first, Received);
        var again  = route.Encode(first, Received);
        route.Acknowledged();

        await Assert.That(Events(again)).IsEquivalentTo(Events(once));
        await Assert.That(Events(route.Encode(second, Received))).IsEquivalentTo(expected);
    }

    [Test]
    public async Task A_batch_that_cannot_be_projected_falls_the_route_back() {
        var deep  = string.Concat(Enumerable.Repeat("[", 58)) + string.Concat(Enumerable.Repeat("]", 58));
        var line  = $$$$"""{"type":"assistant","uuid":"{{{{Id(2)}}}}","timestamp":"2026-11-16T08:00:02Z","message":{"model":"claude-x","content":[{"type":"tool_use","id":"t1","name":"X","input":{"a":{{{{deep}}}}}}]}}""";
        var route = Route();

        await Assert.That(() => route.Encode(Raw(0, line), Received)).Throws<CanonicalEncodingException>();
        await Assert.That(route.Active).IsFalse();
        await Assert.That(() => route.Encode(Raw(0, User(1)), Received)).Throws<InvalidOperationException>();
    }

    [Test]
    public async Task A_replay_that_fails_falls_the_route_back() {
        var deep  = string.Concat(Enumerable.Repeat("[", 58)) + string.Concat(Enumerable.Repeat("]", 58));
        var line  = $$$$"""{"type":"assistant","uuid":"{{{{Id(2)}}}}","timestamp":"2026-11-16T08:00:02Z","message":{"model":"claude-x","content":[{"type":"tool_use","id":"t1","name":"X","input":{"a":{{{{deep}}}}}}]}}""";
        var path  = Tmp.CreateFile("bad.jsonl", $"{User(1)}\n{line}\n");
        var route = Route();

        await Assert.That(() => route.ReplayAsync(path, 2, CancellationToken.None)).Throws<CanonicalEncodingException>();
        await Assert.That(route.Active).IsFalse();
    }
}
