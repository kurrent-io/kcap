using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit;

public class TranscriptBatchBufferTests {
    [Test]
    public async Task SplittingUsesUtf8SizeAndPreservesSourceCoordinates() {
        var line = new string('\u4e00', 600_000);
        var source = new TranscriptBatch {
            SessionId = "parent", AgentId = "child", Vendor = "codex", Strict = true,
            Lines = [line, line, line], LineNumbers = [2, 4, 7]
        };

        var batches = TranscriptBatchBuffer.Split(source).ToArray();

        await Assert.That(batches.Length).IsEqualTo(2);
        await Assert.That(batches[0].LineNumbers!).IsEquivalentTo(new[] { 2, 4 });
        await Assert.That(batches[1].LineNumbers!).IsEquivalentTo(new[] { 7 });
        await Assert.That(batches.All(x => x is { SessionId: "parent", AgentId: "child", Vendor: "codex", Strict: true })).IsTrue();
        await Assert.That(batches[0].MoreFollows).IsTrue();
        await Assert.That(batches[1].MoreFollows).IsFalse();
    }

    [Test]
    public async Task EmptyEnvelopeCanStillCarryMetadata() {
        var batch = new TranscriptBatch { SessionId = "parent", Lines = [], LineNumbers = [] };
        await Assert.That(TranscriptBatchBuffer.Split(batch).Count()).IsEqualTo(1);
    }

    /// <summary>The watcher marks a batch's commits delivered only on a complete send: every chunk carries them, and the
    /// server records them only once it has taken the lines they belong with.</summary>
    [Test]
    [Arguments(new[] { 3, 5 }, 2, true, 5)]
    [Arguments(new[] { 2, 5 }, 1, false, 2)]
    [Arguments(new[] { 3, 4 }, 2, false, 4)]
    public async Task A_send_is_complete_only_when_every_chunk_is_acknowledged_through_its_last_line(int[] acks, int sends, bool complete, int ackedNext) {
        var line   = new string('x', TranscriptBatchBuffer.MaxBytes / 2 + 1);
        var batch  = new TranscriptBatch { SessionId = "s", Lines = [line, line], LineNumbers = [2, 4] };
        var sent   = new List<TranscriptBatch>();

        var outcome = await TranscriptBatchBuffer.SendAsync(batch, chunk => { sent.Add(chunk); return Task.FromResult<int?>(acks[sent.Count - 1]); }, () => true);

        await Assert.That(sent.Count).IsEqualTo(sends);
        await Assert.That(outcome).IsEqualTo(new ChunkedSend(ackedNext, complete));
    }

    [Test]
    public async Task A_transport_without_acknowledgements_completes_once_every_chunk_is_sent() {
        var batch = new TranscriptBatch { SessionId = "s", Lines = ["a"], LineNumbers = [1] };

        await Assert.That(await TranscriptBatchBuffer.SendAsync(batch, _ => Task.FromResult<int?>(null), () => true)).IsEqualTo(new ChunkedSend(null, true));
    }

    [Test]
    public async Task A_refused_chunk_stops_the_send_unfinished() {
        var batch = new TranscriptBatch { SessionId = "s", Lines = ["a"], LineNumbers = [1] };

        await Assert.That(await TranscriptBatchBuffer.SendAsync(batch, _ => throw new InvalidOperationException("sent"), () => false)).IsNull();
    }

    [Test]
    public async Task Fills_at_the_line_cap() {
        var buffer = new TranscriptBatchBuffer();

        for (var i = 0; i < TranscriptBatchBuffer.MaxLines - 1; i++) buffer.Add("x", i, 1);

        await Assert.That(buffer.IsFull).IsFalse();

        buffer.Add("x", TranscriptBatchBuffer.MaxLines - 1, 1);

        await Assert.That(buffer.IsFull).IsTrue();
        await Assert.That(buffer.FirstLineNumber).IsEqualTo(0);
        await Assert.That(buffer.LastLineNumber).IsEqualTo(TranscriptBatchBuffer.MaxLines - 1);
    }

    [Test]
    public async Task Refuses_a_line_that_would_pass_the_byte_budget() {
        var buffer = new TranscriptBatchBuffer();
        buffer.Add("big", 0, 3 * 1024 * 1024);

        await Assert.That(buffer.Fits(1024 * 1024)).IsTrue();
        await Assert.That(buffer.Fits(1024 * 1024 + 1)).IsFalse();

        buffer.Clear();

        await Assert.That(buffer.IsEmpty).IsTrue();
        await Assert.That(buffer.Fits(TranscriptBatchBuffer.MaxBytes)).IsTrue();
    }

    [Test]
    public async Task Measures_a_line_in_utf8_bytes() {
        await Assert.That(TranscriptBatchBuffer.SizeOf("x")).IsEqualTo(1);
        await Assert.That(TranscriptBatchBuffer.SizeOf("é")).IsEqualTo(2);
        await Assert.That(TranscriptBatchBuffer.SizeOf("日")).IsEqualTo(3);
    }
}
