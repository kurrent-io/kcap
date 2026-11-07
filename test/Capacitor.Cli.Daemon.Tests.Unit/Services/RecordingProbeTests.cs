using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>What the probe makes of the server's answers. Only an unknown method is a no, and with the connection up
/// nothing else ends the asking: with no reconnect coming, one failed ask would leave recording off for good.</summary>
public class RecordingProbeTests {
    const string NoSuchMethod = "Failed to invoke 'WatcherDetach' due to an error on the server. HubException: Method does not exist.";

    readonly FakeTimeProvider _time = new(DateTimeOffset.UtcNow);
    readonly RecordingProbe _probe = new();
    readonly List<DateTimeOffset> _asks = [];

    /// <summary>Asks, recording when, and answers with <paramref name="answer"/> given the number of this ask.</summary>
    Task AskAsync(Func<int, Exception?> answer, CancellationToken ct = default) => _probe.AskUntilAnsweredAsync(_ => {
        lock (_asks) _asks.Add(_time.GetUtcNow());

        int ask;
        lock (_asks) ask = _asks.Count;

        return answer(ask) is { } failure ? Task.FromException(failure) : Task.CompletedTask;
    }, _time, ct);

    int Asks {
        get {
            lock (_asks) return _asks.Count;
        }
    }

    async Task UntilAsks(int count, int stepSeconds = 1) => await WaitHarness.PollUntilAsync(() => {
        _time.Advance(TimeSpan.FromSeconds(stepSeconds));

        return Asks >= count;
    });

    [Test]
    public async Task A_server_with_the_method_says_yes() {
        await AskAsync(_ => null).WithinDeadlineAsync("the ask");

        await Assert.That(_probe.Answer).IsTrue();
    }

    [Test]
    public async Task A_server_without_the_method_says_no() {
        await AskAsync(_ => new HubException(NoSuchMethod)).WithinDeadlineAsync("the ask");

        await Assert.That(_probe.Answer).IsFalse();
        await Assert.That(Asks).IsEqualTo(1);
    }

    /// <summary>A server missing some other method says nothing about this one.</summary>
    [Test]
    public async Task Another_methods_failure_is_not_a_no() {
        var asking = AskAsync(ask => ask == 1 ? new HubException("Unknown hub method 'WatcherDetachAll'") : null);

        await UntilAsks(2);
        await asking.WithinDeadlineAsync("the second ask");

        await Assert.That(_probe.Answer).IsTrue();
    }

    [Test]
    public async Task A_failed_ask_is_made_again_until_the_server_answers() {
        var asking = AskAsync(ask => ask < 3 ? new HubException("Busy.") : null);

        await UntilAsks(3);
        await asking.WithinDeadlineAsync("the third ask");

        await Assert.That(_probe.Answer).IsTrue();
    }

    /// <summary>Spaced out, and never by more than the cap.</summary>
    [Test]
    public async Task The_wait_between_asks_grows_to_its_cap() {
        var asking = AskAsync(ask => ask < 9 ? new HubException("Busy.") : null);

        await UntilAsks(9, stepSeconds: 10);
        await asking.WithinDeadlineAsync("the last ask");

        var gaps = _asks.Zip(_asks.Skip(1), (earlier, later) => later - earlier).ToArray();

        await Assert.That(gaps[0] < gaps[3]).IsTrue();
        await Assert.That(gaps.Max() <= RecordingProbe.MaxRetry + TimeSpan.FromSeconds(10)).IsTrue();
        await Assert.That(gaps[^1] >= RecordingProbe.MaxRetry).IsTrue();
    }

    /// <summary>A question to a connection that has gone stops with it: the next one asks for itself.</summary>
    [Test]
    public async Task A_connection_going_away_ends_the_asking() {
        var asking = AskAsync(_ => new HubException("Busy."));
        await UntilAsks(1);

        _probe.Forget();
        await asking.WithinDeadlineAsync("the asking");

        await Assert.That(_probe.Answer).IsNull();
    }

    [Test]
    public async Task A_new_ask_replaces_the_one_before_it() {
        var first = AskAsync(_ => new HubException("Busy."));
        await UntilAsks(1);

        await AskAsync(_ => null).WithinDeadlineAsync("the second ask");
        await first.WithinDeadlineAsync("the first ask to give way");

        await Assert.That(_probe.Answer).IsTrue();
    }
}
