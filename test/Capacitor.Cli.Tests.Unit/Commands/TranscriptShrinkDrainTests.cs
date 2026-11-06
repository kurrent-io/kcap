using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.PrDetection;
using Microsoft.AspNetCore.SignalR.Client;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>
/// The server drops, without an error, a line numbered at or below one it already took, so a read
/// must never lower the watcher's line cursor. Only a reconnect rewind does.
/// </summary>
public class TranscriptShrinkDrainTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }
    [TempDir] public required TempDir Tmp { get; init; }

    WatchCommand Watch => field ??= new(Config.Root, Resolutions.None(Config.Root), TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), new FixedCredentialSource(), TestWatchers.For(Config.Root, Resolutions.None(Config.Root), new FixedCapacitorHttpClient()), new GitProviderRouter(), TimeProvider.System, TestAccounts.None, Home);

    static readonly string Sid = Guid.NewGuid().ToString("N");

    static HubConnection UnconnectedHub() => new HubConnectionBuilder().WithUrl("http://127.0.0.1:1/hubs/sessions").Build();

    static string Lines(int from, int count) =>
        string.Concat(Enumerable.Range(from, count).Select(i => $"{{\"a\":{i}}}\n"));

    string Transcript(int count) => Tmp.CreateFile("t.jsonl", Lines(0, count));

    async Task<IReadOnlyList<string>> Drain(string path, WatchState state) {
        await using var hub = UnconnectedHub();

        return await Watch.DrainNewLines(hub, Sid, path, agentId: null, state, "claude", CancellationToken.None);
    }

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
    [NotInParallel]
    public async Task A_shrink_is_logged_once() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };
        await File.WriteAllTextAsync(path, Lines(0, 4));

        using var capture = ConsoleOutput.StartErrorCapture();
        for (var i = 0; i < 3; i++) await Drain(path, state);

        await Assert.That(capture.GetCapturedError().Split('\n').Count(l => l.Contains("shorter"))).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task A_second_shrink_logs_again() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10, ThresholdReached = true };

        using var capture = ConsoleOutput.StartErrorCapture();
        await File.WriteAllTextAsync(path, Lines(0, 4));
        await Drain(path, state);
        await File.WriteAllTextAsync(path, Lines(0, 10));
        await Drain(path, state);
        await File.WriteAllTextAsync(path, Lines(0, 4));
        await Drain(path, state);

        await Assert.That(capture.GetCapturedError().Split('\n').Count(l => l.Contains("shorter"))).IsEqualTo(2);
    }

    [Test]
    public async Task Reconnect_rewind_resends_exactly_the_gap() {
        var path  = Transcript(10);
        var state = new WatchState { LinesProcessed = 10 };

        await Watch.ApplyReconnectRewindAsync(state, 7, Sid, "claude", path, null, CancellationToken.None);
        await Assert.That(state.LinesProcessed).IsEqualTo(7);

        state.ThresholdReached = false;
        var read = await Drain(path, state);

        await Assert.That(read).IsEquivalentTo(Enumerable.Range(7, 3).Select(i => $"{{\"a\":{i}}}").ToArray());
        await Assert.That(state.BufferedLineNumbers).IsEquivalentTo([7, 8, 9]);
    }
}
