using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Tests.Unit;

/// <summary>
/// The server drops, without an error, a line numbered at or below one it already took, so a read
/// must never lower the watcher's line cursor. Only a reconnect rewind does.
/// </summary>
public class TranscriptShrinkDrainTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    TestRecorders Recorders => field ??= new(Config.Root);

    static readonly string Sid = Guid.NewGuid().ToString("N");

    static string Lines(int from, int count) =>
        string.Concat(Enumerable.Range(from, count).Select(i => $"{{\"a\":{i}}}\n"));

    string Transcript(int count) => Tmp.CreateFile("t.jsonl", Lines(0, count));

    Task<IReadOnlyList<string>> Drain(string path, WatchState state) =>
        Recorders.For(Sid, path, null, "claude").DrainNewLines(state, CancellationToken.None);

    [Test]
    public async Task A_file_shorter_than_the_cursor_keeps_the_cursor() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };
        await File.WriteAllTextAsync(path, Lines(0, 4));

        var sent = await Drain(path, state);

        await Assert.That(sent).IsEmpty();
        await Assert.That(state.LinesProcessed).IsEqualTo(10);
    }

    [Test]
    public async Task Lines_past_the_cursor_after_a_shrink_keep_their_own_numbers() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };
        await File.WriteAllTextAsync(path, Lines(0, 4));
        await Drain(path, state);
        await File.WriteAllTextAsync(path, Lines(0, 13));

        state.ThresholdReached = false;
        await Drain(path, state);

        await Assert.That(state.BufferedLineNumbers).IsEquivalentTo([10, 11, 12]);
        await Assert.That(state.LinesProcessed).IsEqualTo(10);
    }

    [Test]
    public async Task A_shrink_is_logged_once() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };
        await File.WriteAllTextAsync(path, Lines(0, 4));

        for (var i = 0; i < 3; i++) await Drain(path, state);

        await Assert.That(Recorders.Log.Count(l => l.Contains("shorter"))).IsEqualTo(1);
    }

    [Test]
    public async Task A_second_shrink_logs_again() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };

        await File.WriteAllTextAsync(path, Lines(0, 4));
        await Drain(path, state);
        await File.WriteAllTextAsync(path, Lines(0, 10));
        await Drain(path, state);
        await File.WriteAllTextAsync(path, Lines(0, 4));
        await Drain(path, state);

        await Assert.That(Recorders.Log.Count(l => l.Contains("shorter"))).IsEqualTo(2);
    }

    [Test]
    public async Task Reconnect_rewind_resends_exactly_the_gap() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10 };

        await Recorders.For(Sid, path, null, "claude").ApplyReconnectRewindAsync(state, 7, null, CancellationToken.None);
        await Assert.That(state.LinesProcessed).IsEqualTo(7);

        state.ThresholdReached = false;
        var read = await Drain(path, state);

        await Assert.That(read).IsEquivalentTo(Enumerable.Range(7, 3).Select(i => $"{{\"a\":{i}}}").ToArray());
        await Assert.That(state.BufferedLineNumbers).IsEquivalentTo([7, 8, 9]);
    }
}
