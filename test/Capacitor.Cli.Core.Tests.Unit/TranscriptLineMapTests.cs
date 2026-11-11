namespace Capacitor.Cli.Core.Tests.Unit;

/// <summary>
/// After rewrites, file lines are sent above every line sent before, and a sent position maps back to the first
/// file line still sent at or after it, including one whose lines the rewrite dropped.
/// </summary>
public class TranscriptLineMapTests {
    // Ten lines sent, then the file rewritten from line 4; six more sent, then rewritten again from line 6.
    static TranscriptLineMap TwiceRewritten() {
        var map = new TranscriptLineMap();
        map.Rewritten(4, 10, 10);
        map.Rewritten(6, 16, 10);

        return map;
    }

    [Test]
    [Arguments(0, 0)]
    [Arguments(3, 3)]
    [Arguments(4, 10)]
    [Arguments(5, 11)]
    [Arguments(6, 16)]
    [Arguments(9, 19)]
    public async Task A_file_line_is_sent_above_the_lines_before_each_rewrite(int fileLine, int sent) {
        await Assert.That(TwiceRewritten().ToSent(fileLine)).IsEqualTo(sent);
    }

    [Test]
    [Arguments(3, 3)]
    [Arguments(4, 4)]
    [Arguments(7, 4)]
    [Arguments(10, 4)]
    [Arguments(11, 5)]
    [Arguments(13, 6)]
    [Arguments(17, 7)]
    public async Task A_sent_position_maps_to_the_first_file_line_sent_at_or_after_it(int sent, int fileLine) {
        await Assert.That(TwiceRewritten().ToFile(sent)).IsEqualTo(fileLine);
    }

    [Test]
    public async Task A_whole_file_rewrite_numbers_every_line_from_the_cursor() {
        var map = new TranscriptLineMap();
        map.Rewritten(0, 5, 5);

        await Assert.That(map.ToSent(0)).IsEqualTo(5);
        await Assert.That(map.ToFile(3)).IsEqualTo(0);
    }

    [Test]
    public async Task The_first_changed_line_is_found_and_a_blank_line_compares_as_blank() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("t.jsonl", "a\n\nc\nd\n");
        var       map  = new TranscriptLineMap();
        map.Observe(["a", "c", "d"], [0, 2, 3], 4);

        await File.WriteAllTextAsync(path, "a\n\nx\n");

        await Assert.That(await map.FirstChangedLineAsync(path, 4, CancellationToken.None)).IsEqualTo(2);
        await Assert.That(map.Matches(1, null)).IsTrue();
    }

    [Test]
    public async Task A_file_shorter_than_the_lines_read_changed_where_it_ends() {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("t.jsonl", "a\nb\n");
        var       map  = new TranscriptLineMap();
        map.Observe(["a", "b", "c"], [0, 1, 2], 3);

        await Assert.That(await map.FirstChangedLineAsync(path, 3, CancellationToken.None)).IsEqualTo(2);
    }

    /// <summary>A second rewrite found before the cursor moved, further back than the first, still holds back the
    /// lines sent before the first.</summary>
    [Test]
    public async Task Copies_stay_held_back_across_rewrites_found_one_after_another() {
        var map  = new TranscriptLineMap();
        var sent = Enumerable.Range(0, 10).Select(i => $"a{i}").ToArray();
        map.Observe(sent, Enumerable.Range(0, 10).ToArray(), 10);
        map.Rewritten(4, 10, 10);
        map.Rewritten(3, 10, 4);

        var kept = map.KeptCopies(sent[3..], Enumerable.Range(3, 7).ToArray(), 3);

        await Assert.That(kept).IsEquivalentTo(Enumerable.Repeat(true, 7).ToArray());
    }
}
