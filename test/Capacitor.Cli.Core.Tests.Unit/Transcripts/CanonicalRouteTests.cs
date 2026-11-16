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
