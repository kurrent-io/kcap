using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Copilot;

/// <summary>
/// Restoring a checkpoint rewrites Copilot's <c>events.jsonl</c> without the turns after it, and the next prompt is
/// appended to the shorter file. The server drops a line numbered at or below one it took, so every line written
/// after the rewrite must go out numbered above the lines already sent, and no line already sent goes out again.
/// </summary>
public class CopilotRewrittenTranscriptTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    TestRecorders Recorders => field ??= new(Config.Root);

    static readonly string Sid = Guid.NewGuid().ToString("N");

    static string Line(string turn, int i) => $"{{\"type\":\"hook.end\",\"id\":\"{turn}-{i}\",\"data\":{{}}}}";

    static string Lines(string turn, int from, int count) =>
        string.Concat(Enumerable.Range(from, count).Select(i => Line(turn, i) + "\n"));

    async Task<(string Path, WatchState State)> Sent(int count) {
        var path  = Tmp.CreateFile("events.jsonl", Lines("a", 0, count));
        var state = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;
        await Drain(path, state);

        return (path, state);
    }

    Task<IReadOnlyList<string>> Drain(string path, WatchState state, string vendor = "copilot") =>
        Recorders.For(Sid, path, null, vendor).DrainNewLines(state, CancellationToken.None);

    (int Number, string Line)[] SentLines => Recorders.Transport.Batches
        .SelectMany(b => b.LineNumbers!.Zip(b.Lines, (n, l) => (n, l))).ToArray();

    [Test]
    public async Task Lines_appended_after_a_rewrite_seen_shorter_are_sent_above_the_cursor() {
        var (path, state) = await Sent(10);

        await File.WriteAllTextAsync(path, Lines("a", 0, 4));
        await Drain(path, state);
        await File.AppendAllTextAsync(path, Lines("b", 0, 3));
        await Drain(path, state);

        await Assert.That(SentLines.Skip(10).ToArray())
            .IsEquivalentTo(new[] { (10, Line("b", 0)), (11, Line("b", 1)), (12, Line("b", 2)) });
        await Assert.That(state.LinesProcessed).IsEqualTo(13);
    }

    /// <summary>The rewritten file can grow past the old cursor before any poll sees it shorter.</summary>
    [Test]
    public async Task A_rewrite_that_regrew_past_the_cursor_between_polls_sends_every_new_line() {
        var (path, state) = await Sent(10);

        await File.WriteAllTextAsync(path, Lines("a", 0, 4) + Lines("b", 0, 8));
        await Drain(path, state);

        await Assert.That(SentLines.Skip(10).Select(s => s.Line).ToArray())
            .IsEquivalentTo(Enumerable.Range(0, 8).Select(i => Line("b", i)).ToArray());
        await Assert.That(SentLines.Skip(10).Select(s => s.Number).ToArray())
            .IsEquivalentTo(Enumerable.Range(10, 8).ToArray());
    }

    [Test]
    public async Task An_appended_transcript_keeps_its_own_line_numbers() {
        var (path, state) = await Sent(10);

        await File.AppendAllTextAsync(path, Lines("a", 10, 2));
        await Drain(path, state);

        await Assert.That(SentLines.Select(s => s.Number).ToArray()).IsEquivalentTo(Enumerable.Range(0, 12).ToArray());
    }

    /// <summary>A live poll holds back the final line a final drain consumed without its newline, which reads as a
    /// file shorter than the cursor.</summary>
    [Test]
    public async Task A_final_line_consumed_without_its_newline_is_not_a_rewrite() {
        var path  = Tmp.CreateFile("events.jsonl", Lines("a", 0, 9) + Line("a", 9));
        var state = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;
        await Recorders.For(Sid, path, null, "copilot").DrainNewLines(state, CancellationToken.None, isFinalDrain: true);

        await Drain(path, state);
        await File.AppendAllTextAsync(path, "\n" + Lines("a", 10, 1));
        await Drain(path, state);

        await Assert.That(SentLines.Select(s => s.Number).ToArray()).IsEquivalentTo(Enumerable.Range(0, 11).ToArray());
        await Assert.That(Recorders.Log.Any(l => l.Contains("rewritten"))).IsFalse();
    }

    /// <summary>A watcher resumed on a file rewritten shorter while nothing watched it.</summary>
    [Test]
    public async Task A_resumed_watcher_numbers_lines_after_a_shorter_file_above_the_server_frontier() {
        var path  = Tmp.CreateFile("events.jsonl", Lines("a", 0, 4));
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };
        Recorders.Transport.IsConnected = true;

        await Drain(path, state);
        await File.AppendAllTextAsync(path, Lines("b", 0, 2));
        await Drain(path, state);

        await Assert.That(SentLines).IsEquivalentTo(new[] { (10, Line("b", 0)), (11, Line("b", 1)) });
    }

    [Test]
    public async Task A_reconnect_rewind_past_a_rewrite_resends_only_the_lines_after_it() {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, Lines("a", 0, 4) + Lines("b", 0, 3));
        await Drain(path, state);

        await Recorders.For(Sid, path, null, "copilot").ApplyReconnectRewindAsync(state, 11, null, CancellationToken.None);
        await Drain(path, state);

        await Assert.That(SentLines.Skip(13).ToArray()).IsEquivalentTo(new[] { (11, Line("b", 1)), (12, Line("b", 2)) });
    }

    [Test]
    public async Task The_shutdown_tail_after_a_rewrite_is_spooled_above_the_cursor() {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, Lines("a", 0, 4));
        await Drain(path, state);
        await File.AppendAllTextAsync(path, Lines("b", 0, 2));
        var spool = new TranscriptSpool(Tmp.PathTo("spool"), time: TimeProvider.System);

        await Recorders.For(Sid, path, null, "copilot").SpoolUndeliveredTranscriptTailAsync(
            spool, state.LinesProcessed, new CommitObservation.Uncovered(), CancellationToken.None, lineMap: state.LineMap);

        var replay = new List<string>();
        await spool.DrainAsync(Sid, body => { replay.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            () => false, CancellationToken.None);
        var numbers = System.Text.Json.Nodes.JsonNode.Parse(replay.Single())!["line_numbers"]!.AsArray().Select(n => n!.GetValue<int>());
        await Assert.That(numbers.ToArray()).IsEquivalentTo(new[] { 10, 11 });
    }
}
