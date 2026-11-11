using Capacitor.Cli.Core.Http;
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
        await Assert.That(new TranscriptRenumbering(Config.Root, "http://127.0.0.1:1").Read(Sid)).IsEqualTo((4, 6));
    }

    [Test]
    public async Task A_rewrite_after_trailing_blank_lines_sends_the_line_written_where_they_were() {
        var path  = Tmp.CreateFile("events.jsonl", Lines("a", 0, 4) + new string('\n', 6));
        var state = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;
        await Drain(path, state);

        await File.WriteAllTextAsync(path, Lines("a", 0, 4) + Lines("b", 0, 1));
        await Drain(path, state);

        await Assert.That(SentLines.Skip(4).ToArray()).IsEquivalentTo(new[] { (10, Line("b", 0)) });
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

    /// <summary>The final drain can be cut off before its read, leaving the spool the first to see the rewrite.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task The_shutdown_tail_after_a_rewrite_is_spooled_above_the_cursor(bool polledSinceRewrite) {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, Lines("a", 0, 4));
        if (polledSinceRewrite) await Drain(path, state);
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

    // Line 9, just before the cursor, is left as it was.
    static string EditedBeforeTheLastLine => Lines("a", 0, 5) + Lines("b", 5, 4) + Line("a", 9) + "\n";

    [Test]
    public async Task The_final_drain_catches_a_rewrite_that_kept_the_line_before_the_cursor() {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, EditedBeforeTheLastLine);

        await Recorders.For(Sid, path, null, "copilot").DrainNewLines(state, CancellationToken.None, isFinalDrain: true);

        await Assert.That(SentLines.Skip(10).ToArray()).IsEquivalentTo(
            Enumerable.Range(5, 4).Select(i => (i + 5, Line("b", i))).ToArray());
    }

    [Test]
    public async Task Live_polls_catch_a_rewrite_that_kept_the_line_before_the_cursor() {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, EditedBeforeTheLastLine);

        for (var i = 0; i < TranscriptRecorder.RewrittenPrefixCheckEveryNPolls; i++) await Drain(path, state);

        await Assert.That(SentLines.Skip(10).Select(s => s.Number).ToArray()).IsEquivalentTo(Enumerable.Range(10, 4).ToArray());
    }

    /// <summary>A line appended after an in-place edit goes out before a later poll finds the edit.</summary>
    [Test]
    public async Task A_line_sent_before_its_rewrite_was_found_is_not_sent_again() {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, EditedBeforeTheLastLine + Lines("a", 10, 1));

        for (var i = 0; i < TranscriptRecorder.RewrittenPrefixCheckEveryNPolls; i++) await Drain(path, state);

        await Assert.That(SentLines.Skip(10).ToArray()).IsEquivalentTo(
            Enumerable.Range(5, 4).Select(i => (i + 6, Line("b", i))).Prepend((10, Line("a", 10))).ToArray());
    }

    /// <summary>Only the copies a rewrite kept of lines already sent are held back, each once: an identical record
    /// written again is a new line.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_identical_record_written_again_after_a_rewrite_is_sent(bool insideTheRewrite) {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, insideTheRewrite
            ? Lines("a", 0, 5) + Lines("b", 5, 3) + Line("a", 9) + "\n" + Line("a", 9) + "\n"
            : EditedBeforeTheLastLine);
        await Recorders.For(Sid, path, null, "copilot").DrainNewLines(state, CancellationToken.None, isFinalDrain: true);
        if (!insideTheRewrite) await File.AppendAllTextAsync(path, Line("a", 9) + "\n");
        await Drain(path, state);

        var edited = insideTheRewrite ? 3 : 4;
        await Assert.That(SentLines.Skip(10).Select(s => s.Line).ToArray()).IsEquivalentTo(
            Enumerable.Range(5, edited).Select(i => Line("b", i)).Append(Line("a", 9)).ToArray());
    }

    [Test]
    public async Task A_kept_copy_stays_held_back_when_the_send_after_the_rewrite_fails() {
        var (path, state) = await Sent(10);
        await File.WriteAllTextAsync(path, EditedBeforeTheLastLine);

        Recorders.Transport.IsConnected = false;
        await Recorders.For(Sid, path, null, "copilot").DrainNewLines(state, CancellationToken.None, isFinalDrain: true);
        Recorders.Transport.IsConnected = true;
        await Drain(path, state);

        await Assert.That(SentLines.Skip(10).ToArray()).IsEquivalentTo(
            Enumerable.Range(5, 4).Select(i => (i + 5, Line("b", i))).ToArray());
    }

    /// <summary>A file that changes under every read leaves nothing the spool can trust, so the session is marked for
    /// import rather than spooled as having no tail.</summary>
    [Test]
    public async Task A_shutdown_tail_that_never_reads_stable_marks_the_session_for_import() {
        var path    = Tmp.CreateFile("events.jsonl", Lines("a", 0, 10));
        var rewrite = -1;
        var recorder = new TranscriptRecorder(
            new RecordingSource(Sid, null, path, "copilot", null), Recorders.Transport, new NoStamps(), TimeProvider.System,
            new RecorderContext(new CapacitorServer("http://127.0.0.1:1", Config.Root, Resolutions.None(Config.Root)),
                Recorders.Housekeeping, TimeProvider.System, _ => { }, () => { })) {
            // Each read keeps fewer of the original lines than the last.
            OpenTranscript = p => {
                if (rewrite >= 0) File.WriteAllText(p, Lines("a", 0, 6 - 2 * rewrite) + Lines($"r{rewrite}", 0, 3));
                if (rewrite >= 0) rewrite++;
                return new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            }
        };
        var state = new WatchState { ThresholdReached = true };
        Recorders.Transport.IsConnected = true;
        await recorder.DrainNewLines(state, CancellationToken.None);
        rewrite = 0;
        var spool = new TranscriptSpool(Tmp.PathTo("spool"), time: TimeProvider.System);

        var result = await recorder.SpoolUndeliveredTranscriptTailAsync(
            spool, state.LinesProcessed, new CommitObservation.Uncovered(), CancellationToken.None, lineMap: state.LineMap);

        await Assert.That(result).IsEqualTo(TranscriptSpool.AppendResult.MarkedNeedsImport);
        await Assert.That(spool.NeedsImport(Sid)).IsTrue();
    }
}
