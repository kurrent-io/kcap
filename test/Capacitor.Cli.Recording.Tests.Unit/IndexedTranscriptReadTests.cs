using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit;

/// <summary>
/// A poll that carries a <see cref="TranscriptLineIndex"/> must return exactly what a full read
/// from byte 0 returns, while reading only the bytes past its line cursor.
/// </summary>
public class IndexedTranscriptReadTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const IncompleteFinalLinePolicy Hold    = IncompleteFinalLinePolicy.Hold;
    const IncompleteFinalLinePolicy Consume = IncompleteFinalLinePolicy.ConsumeIfComplete;

    static async Task<(NewTranscriptLines Result, long BytesRead)> Read(
            string path, int linesProcessed, IncompleteFinalLinePolicy policy, TranscriptLineIndex? index) {
        await using var stream = new CountingFileStream(path);
        var result = await TranscriptReader.ReadNewCompleteLinesAsync(stream, linesProcessed, policy, default, lineOffsets: index);

        return (result, stream.BytesRead);
    }

    static async Task AssertSame(NewTranscriptLines expected, NewTranscriptLines actual, string context) {
        await Assert.That(actual.Lines).IsEquivalentTo(expected.Lines).Because(context);
        await Assert.That(actual.LineNumbers).IsEquivalentTo(expected.LineNumbers).Because(context);
        await Assert.That(actual.NextPosition).IsEqualTo(expected.NextPosition).Because(context);
        await Assert.That(actual.HeldIncompleteFinalLine).IsEqualTo(expected.HeldIncompleteFinalLine).Because(context);
    }

    static byte[] Line(int i, int padding) =>
        Encoding.UTF8.GetBytes($"{{\"i\":{i},\"pad\":\"{new string('x', padding)}\"}}\n");

    string WriteLines(int count, int padding) {
        using var buffer = new MemoryStream();
        for (var i = 0; i < count; i++) buffer.Write(Line(i, padding));

        return Tmp.CreateFile("big.jsonl", Encoding.UTF8.GetString(buffer.ToArray()));
    }

    [Test]
    public async Task An_idle_poll_reads_a_bounded_number_of_bytes_whatever_the_file_size() {
        var path  = WriteLines(2000, 500);
        var index = new TranscriptLineIndex();

        var first = await Read(path, 0, Hold, index);
        var idle  = await Read(path, first.Result.NextPosition, Hold, index);

        await Assert.That(new FileInfo(path).Length).IsGreaterThan(1_000_000);
        await Assert.That(idle.Result.Lines).IsEmpty();
        await Assert.That(idle.Result.NextPosition).IsEqualTo(2000);
        await Assert.That(idle.BytesRead).IsLessThan(16 * 1024);
    }

    [Test]
    public async Task A_poll_after_an_append_reads_the_appended_bytes_not_the_file() {
        var path  = WriteLines(2000, 500);
        var index = new TranscriptLineIndex();
        var first = await Read(path, 0, Hold, index);

        await using (var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) {
            for (var i = 2000; i < 2010; i++) append.Write(Line(i, 500));
        }

        var next = await Read(path, first.Result.NextPosition, Hold, index);

        await Assert.That(next.Result.LineNumbers).IsEquivalentTo(Enumerable.Range(2000, 10).ToArray());
        await Assert.That(next.BytesRead).IsLessThan(10 * 600 + 16 * 1024);
    }

    [Test]
    public async Task A_rewound_cursor_reads_from_its_own_line() {
        var path  = WriteLines(2000, 500);
        var index = new TranscriptLineIndex();
        await Read(path, 0, Hold, index);

        var rewound   = await Read(path, 1990, Hold, index);
        var reference = await Read(path, 1990, Hold, null);

        await AssertSame(reference.Result, rewound.Result, "rewind to 1990");
        await Assert.That(rewound.BytesRead).IsLessThan(10 * 600 + 16 * 1024);
    }

    [Test]
    public async Task A_rewrite_before_the_indexed_end_falls_back_to_a_full_read() {
        var path  = Tmp.CreateFile("rewrite.jsonl", "{\"a\":1}\n{\"b\":2}\n");
        var index = new TranscriptLineIndex();
        await Read(path, 2, Hold, index);

        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"x\":2}\n{\"y\":3}\n{\"c\":4}\n");

        var indexed   = await Read(path, 2, Hold, index);
        var reference = await Read(path, 2, Hold, null);

        await AssertSame(reference.Result, indexed.Result, "rewrite");
    }

    // Separators StreamReader splits on, bytes it decodes as replacement characters, and blank
    // and unterminated lines, so the index has to agree with StreamReader on every line number.
    static readonly byte[][] Fragments = [
        "{\"k\":1}"u8.ToArray(), "{\"s\":\"é漢😀\"}"u8.ToArray(), "\n"u8.ToArray(), "\n"u8.ToArray(), "\n"u8.ToArray(),
        "\r\n"u8.ToArray(), "\r"u8.ToArray(), "   "u8.ToArray(), [0xC3], [0xFF], [0xE6, 0xBC], "{\"t\":"u8.ToArray(),
    ];

    static byte[] RandomChunk(Random rng) {
        using var chunk = new MemoryStream();
        var parts = rng.Next(1, 12);
        for (var i = 0; i < parts; i++) chunk.Write(Fragments[rng.Next(Fragments.Length)]);

        return chunk.ToArray();
    }

    [Test]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    public async Task Indexed_reads_match_full_reads_through_appends_rewinds_and_rewrites(int seed) {
        var rng     = new Random(seed);
        var path    = Tmp.CreateFile($"fuzz-{seed}.jsonl", seed % 4 == 0 ? "\uFEFF" : "");
        var content = new List<byte>(await File.ReadAllBytesAsync(path));
        var index   = new TranscriptLineIndex();
        var cursor  = 0;

        for (var step = 0; step < 300; step++) {
            var op = rng.Next(20);

            if (op < 14) {
                var chunk = RandomChunk(rng);
                content.AddRange(chunk);
                await using var append = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                await append.WriteAsync(chunk);
            } else if (op < 16 && content.Count > 0) {
                var at = rng.Next(content.Count);
                content[at] = Fragments[rng.Next(Fragments.Length)][0];
                await File.WriteAllBytesAsync(path, content.ToArray());
            } else if (op < 17 && content.Count > 0) {
                content = content.Take(rng.Next(content.Count + 1)).ToList();
                await File.WriteAllBytesAsync(path, content.ToArray());
            }

            var policy    = rng.Next(4) == 0 ? Consume : Hold;
            var reference = await Read(path, cursor, policy, null);
            var indexed   = await Read(path, cursor, policy, index);

            await AssertSame(reference.Result, indexed.Result, $"seed {seed} step {step} cursor {cursor}");

            cursor = rng.Next(10) switch {
                < 6 => reference.Result.NextPosition,
                < 8 => cursor,
                8   => rng.Next(reference.Result.NextPosition + 1),
                _   => reference.Result.NextPosition + rng.Next(3),
            };
        }
    }

    [Test]
    public async Task A_utf16_transcript_is_read_the_same_as_a_full_read() {
        var path = Tmp.PathTo("utf16.jsonl");
        await File.WriteAllTextAsync(path, "{\"a\":1}\n{\"b\":2}\n", Encoding.Unicode);
        var index = new TranscriptLineIndex();

        var first = await Read(path, 0, Hold, index);
        await File.AppendAllTextAsync(path, "{\"c\":3}\n", Encoding.Unicode);

        var indexed   = await Read(path, first.Result.NextPosition, Hold, index);
        var reference = await Read(path, first.Result.NextPosition, Hold, null);

        await AssertSame(reference.Result, indexed.Result, "utf-16");
    }

    [Test]
    [Arguments("")]
    [Arguments("{\"a\":1}\n")]
    [Arguments("{\"a\":1}\n{\"b\":2}")]
    [Arguments("{\"a\":1}\n{\"b\":2")]
    [Arguments("{\"a\":1}\n   ")]
    [Arguments("{\"a\":1")]
    [Arguments("\uFEFF{\"a\":1}")]
    [Arguments("UTF16")]
    [Arguments("LONG")]
    [Arguments("LONG-PARTIAL")]
    public async Task The_final_line_read_decides_completeness_as_the_whole_file_does(string content) {
        var big = "{\"s\":\"" + new string('y', 200_000) + "\"}";
        content = content switch {
            "LONG"         => "{\"a\":1}\n" + big,
            "LONG-PARTIAL" => "{\"a\":1}\n" + big[..^2],
            _              => content,
        };
        var path = Tmp.CreateFile($"final-{Guid.NewGuid():N}.jsonl", content);
        if (content == "UTF16") await File.WriteAllTextAsync(path, "{\"a\":1}\n", Encoding.Unicode);

        var expected = TranscriptReader.IsFinalLineComplete(await File.ReadAllTextAsync(path));
        var actual   = TranscriptReader.IsFinalLineComplete(await TranscriptReader.ReadFinalLineSharedAsync(path));

        await Assert.That(actual).IsEqualTo(expected);
    }

    [Test]
    public async Task A_transcript_replaced_by_utf8_is_indexed_again() {
        var path  = Tmp.PathTo("was-utf16.jsonl");
        var index = new TranscriptLineIndex();
        await File.WriteAllTextAsync(path, "{\"a\":1}\n", Encoding.Unicode);
        await Read(path, 0, Hold, index);

        await File.WriteAllTextAsync(path, string.Concat(Enumerable.Range(0, 2000).Select(i => $"{{\"i\":{i},\"p\":\"{new string('x', 500)}\"}}\n")));
        var first = await Read(path, 0, Hold, index);
        var idle  = await Read(path, first.Result.NextPosition, Hold, index);

        await Assert.That(idle.BytesRead).IsLessThan(16 * 1024);
    }

    [Test]
    public async Task A_line_cut_inside_a_multibyte_character_is_held_until_complete() {
        var whole = "{\"s\":\"é漢😀\"}\n"u8.ToArray();
        var head  = "{\"a\":1}\n"u8.ToArray();
        var cut   = head.Length + Array.IndexOf(whole, (byte)0xE6) + 2;
        var path  = Tmp.PathTo("cut.jsonl");
        var index = new TranscriptLineIndex();
        await File.WriteAllBytesAsync(path, [.. head, .. whole[..(cut - head.Length)]]);

        var held = await Read(path, 0, Hold, index);

        await File.WriteAllBytesAsync(path, [.. head, .. whole]);
        var done      = await Read(path, held.Result.NextPosition, Hold, index);
        var reference = await Read(path, held.Result.NextPosition, Hold, null);

        await Assert.That(held.Result.Lines).IsEquivalentTo(["{\"a\":1}"]);
        await Assert.That(held.Result.NextPosition).IsEqualTo(1);
        await Assert.That(done.Result.Lines).IsEquivalentTo(["{\"s\":\"é漢😀\"}"]);
        await Assert.That(done.Result.LineNumbers).IsEquivalentTo([1]);
        await AssertSame(reference.Result, done.Result, "completed multibyte line");
    }

    // 10_000 lines of ~1 KB.
    string TenMegabytes() => WriteLines(10_000, 1000);

    [Test]
    public async Task The_final_wait_reads_only_the_tail() {
        var path = TenMegabytes();
        await using var stream = new CountingFileStream(path);

        var tail = await TranscriptReader.ReadFinalLineAsync(stream, path);

        await Assert.That(new FileInfo(path).Length).IsGreaterThan(10_000_000);
        await Assert.That(TranscriptReader.IsFinalLineComplete(tail)).IsTrue();
        await Assert.That(stream.BytesRead).IsLessThan(64 * 1024 + 1024);
    }

    [Test]
    public async Task Spooling_the_tail_reads_only_the_tail() {
        var path  = TenMegabytes();
        var index = new TranscriptLineIndex();
        var first = await Read(path, 0, Consume, index);

        var again = await Read(path, first.Result.NextPosition, Consume, index);

        await Assert.That(first.Result.NextPosition).IsEqualTo(10_000);
        await Assert.That(again.Result.Lines).IsEmpty();
        await Assert.That(again.BytesRead).IsLessThan(64 * 1024);
    }

    [Test]
    public async Task A_resume_reads_the_file_once() {
        var path  = TenMegabytes();
        var index = new TranscriptLineIndex();

        var first  = await Read(path, 9_990, Hold, index);
        var second = await Read(path, first.Result.NextPosition, Hold, index);

        await Assert.That(first.Result.LineNumbers).IsEquivalentTo(Enumerable.Range(9_990, 10).ToArray());
        await Assert.That(first.BytesRead).IsGreaterThan(5_000_000);
        await Assert.That(second.BytesRead).IsLessThan(64 * 1024);
    }
}
