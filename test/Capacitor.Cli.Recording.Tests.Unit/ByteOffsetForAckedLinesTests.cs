namespace Capacitor.Cli.Recording.Tests.Unit;

public class ByteOffsetForAckedLinesTests {
    // ByteOffsetForAckedLines: maps a server-acked LINE count to the byte
    // offset within the guard's verified range, so the checkpoint advances only as far as the ack
    // actually covers (a partially-disposed batch must not have its unacked
    // tail bytes checkpointed as if delivered).
    [Test]
    public async Task ByteOffsetForAckedLines_zero_acked_returns_the_range_start() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n");

        var offset = TranscriptReader.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 0);

        await Assert.That(offset).IsEqualTo(100L);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_partial_ack_stops_at_the_nth_newline() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n"); // 6+6+6 bytes

        // Only the first line acked — offset should land right after its newline (byte 6),
        // relative to the range start.
        var offset = TranscriptReader.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 1);

        await Assert.That(offset).IsEqualTo(106L);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_two_of_three_acked_stops_after_the_second_newline() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n"); // 6+6+6 bytes

        var offset = TranscriptReader.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 2);

        await Assert.That(offset).IsEqualTo(112L);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_full_ack_consumes_the_whole_range() {
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\nline2\n");

        var offset = TranscriptReader.ByteOffsetForAckedLines(range, rangeStartOffset: 100, ackedLineCount: 3);

        await Assert.That(offset).IsEqualTo(100L + range.Length);
    }

    [Test]
    public async Task ByteOffsetForAckedLines_acked_more_than_available_newlines_still_bounded_by_the_range() {
        // Defensive: an ack count exceeding what this range accounts for must not read/return
        // past the range's own length.
        var range = System.Text.Encoding.UTF8.GetBytes("line0\nline1\n");

        var offset = TranscriptReader.ByteOffsetForAckedLines(range, rangeStartOffset: 0, ackedLineCount: 99);

        await Assert.That(offset).IsEqualTo(range.Length);
    }
}
