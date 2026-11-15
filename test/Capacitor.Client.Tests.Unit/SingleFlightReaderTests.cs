namespace Capacitor.Client.Tests.Unit;

public class SingleFlightReaderTests {
    static Task Inline(Action action) {
        action();
        return Task.CompletedTask;
    }

    sealed class Harness {
        public List<(string State, TaskCompletionSource<string> Result)> Reads { get; } = [];
        public List<string> Applied { get; } = [];
        public SingleFlightReader<string, string> Reader { get; }

        public Harness(Func<string, string?>? cached = null) {
            Reader = new SingleFlightReader<string, string>(
                (state, ct) => {
                    var result = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Reads.Add((state, result));
                    return result.Task;
                },
                ex => $"error: {ex.Message}",
                Applied.Add,
                "test",
                Inline,
                cached: cached);
        }
    }

    [Test]
    public async Task A_result_for_a_superseded_state_is_not_applied() {
        var h = new Harness();

        h.Reader.Switch("a");
        h.Reader.Switch("b");
        h.Reads[0].Result.SetResult("a result");
        await h.Reads[0].Result.Task;
        h.Reads[1].Result.SetResult("b result");
        await (h.Reader.PendingReadForTesting ?? Task.CompletedTask);

        await Assert.That(h.Applied).IsEquivalentTo(["b result"]);
        await h.Reader.TeardownAsync();
    }

    [Test]
    public async Task A_refresh_during_a_read_runs_one_follow_up() {
        var h = new Harness();

        h.Reader.Switch("a");
        h.Reader.Refresh();
        h.Reader.Refresh();
        h.Reads[0].Result.SetResult("first");
        await (h.Reader.PendingReadForTesting ?? Task.CompletedTask);

        await Assert.That(h.Reads.Count).IsEqualTo(2);
        h.Reads[1].Result.SetResult("second");
        await (h.Reader.PendingReadForTesting ?? Task.CompletedTask);
        await Assert.That(h.Reads.Count).IsEqualTo(2);
        await Assert.That(h.Applied).IsEquivalentTo(["first", "second"]);
        await h.Reader.TeardownAsync();
    }

    [Test]
    public async Task A_cached_result_is_applied_before_the_read() {
        var h = new Harness(state => state == "a" ? "cached a" : null);

        h.Reader.Switch("a");
        await Assert.That(h.Applied).IsEquivalentTo(["cached a"]);

        h.Reads[0].Result.SetResult("fresh a");
        await (h.Reader.PendingReadForTesting ?? Task.CompletedTask);
        await Assert.That(h.Applied).IsEquivalentTo(["cached a", "fresh a"]);
        await h.Reader.TeardownAsync();
    }

    [Test]
    public async Task A_throwing_read_applies_the_error_result() {
        var h = new Harness();

        h.Reader.Switch("a");
        h.Reads[0].Result.SetException(new InvalidOperationException("boom"));
        await (h.Reader.PendingReadForTesting ?? Task.CompletedTask);

        await Assert.That(h.Applied).IsEquivalentTo(["error: boom"]);
        await h.Reader.TeardownAsync();
    }

    [Test]
    public async Task Nothing_is_applied_after_teardown() {
        var h = new Harness();

        h.Reader.Switch("a");
        var pending = h.Reader.PendingReadForTesting;
        await h.Reader.TeardownAsync(waitForPending: false);
        h.Reads[0].Result.TrySetResult("late");
        await (pending ?? Task.CompletedTask);

        await Assert.That(h.Applied).IsEmpty();
    }
}
