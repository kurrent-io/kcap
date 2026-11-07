using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Capacitor.Cli.Recording;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The transport against a real hub that runs a connection's calls side by side, because what is at stake is
/// the order they arrive in and what <c>InvokeAsync</c> does with a token: no fake can show either.</summary>
[ParallelLimiter<SubprocessLimit>]
public class ServerRecorderTransportTests {
    const string SessionA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SessionB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";

    static readonly RecordingSource A = new(SessionA, null, "/a.jsonl", "claude", null);
    static readonly RecordingSource B = new(SessionB, null, "/b.jsonl", "claude", null);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    ServerRecorderTransport Transport(RecordingHubHost hub) => new(hub.Connection(Config), TimeProvider.System, _ => { });

    static TranscriptBatch Batch(string session, int line) => new() { SessionId = session, Lines = ["{}"], LineNumbers = [line] };

    /// <summary>The server's gate is per source, so one stuck batch is that source's alone, and its bound has to read as
    /// a failed send: the recorder retries a failure and treats a cancellation as its own stop.</summary>
    [Test]
    public async Task A_batch_the_server_never_answers_fails_as_a_send_and_the_next_goes_through() {
        var             time      = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config), time, _ => { });
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.BatchHold = line => line == 3 ? release.Task : null;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");

        var stuck = transport.SendBatchAsync(Batch(SessionA, 3), CancellationToken.None);
        await hub.WaitForAsync(h => h.Calls.Contains("batch 3"), "the batch to reach the server");
        await Assert.That(stuck.IsCompleted).IsFalse();

        time.Advance(ServerRecorderTransport.SendBudget);

        Exception? failure = null;

        try {
            await stuck.WithinDeadlineAsync("the send");
        } catch (Exception ex) {
            failure = ex;
        }

        await Assert.That(failure).IsTypeOf<TimeoutException>();
        await Assert.That(failure!.Message).Contains("batch");
        await transport.SendBatchAsync(Batch(SessionB, 4), CancellationToken.None).WithinDeadlineAsync("the next batch");
        await Assert.That(hub.Calls).Contains("batch 4");
        release.SetResult();
    }

    [Test]
    public async Task A_send_stopped_by_its_caller_stays_a_cancellation() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var       stop      = new CancellationTokenSource();
        hub.BatchHold = _ => release.Task;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        var send = transport.SendBatchAsync(Batch(SessionA, 1), stop.Token);
        await hub.WaitForAsync(h => h.Calls.Contains("batch 1"), "the batch to reach the server");

        await stop.CancelAsync();

        await Assert.That(async () => await send).Throws<OperationCanceledException>();
        release.SetResult();
    }

    /// <summary>Left open, the server keeps a connection with no source in its owner's UI topics, and sends it all of them.</summary>
    [Test]
    public async Task The_connection_stops_with_its_last_source() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("A's connect");
        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("B's connect");

        await transport.DetachAsync(A, CancellationToken.None).WithinDeadlineAsync("A's detach");
        await Assert.That(transport.IsConnected).IsTrue();

        await transport.DetachAsync(B, CancellationToken.None).WithinDeadlineAsync("B's detach");
        await hub.WaitForAsync(h => h.OpenConnections == 0, "the connection to stop");

        await Assert.That(transport.IsConnected).IsFalse();
    }

    /// <summary>The source left while the connection was down, so nothing but the connection itself is left to come back.</summary>
    [Test]
    public async Task A_connection_that_lost_its_last_source_while_down_stays_down() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        await hub.CloseCleanlyAsync();

        // Against a connection on its way down the detach can fail, which a host takes in its stride.
        await transport.DetachAsync(A, CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);

        await WaitHarness.PollUntilAsync(() => !transport.IsConnected && hub.OpenConnections == 0);
    }

    /// <summary>A stop and the start behind it can't overlap, and the client's close can land behind the new start.</summary>
    [Test]
    public async Task A_source_behind_the_last_one_starts_the_connection_again() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);

        for (var round = 0; round < 5; round++) {
            await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
            await transport.DetachAsync(A, CancellationToken.None).WithinDeadlineAsync("the detach");
        }

        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("the last connect");

        await Assert.That(transport.IsConnected).IsTrue();
        await Assert.That(hub.Calls).Contains($"connect {SessionB}");
    }

    /// <summary>A connection holds the identity it negotiated, so only a new one stops a source sending as the old
    /// account, and each recorder has to register on it.</summary>
    [Test]
    public async Task A_new_sign_in_restarts_the_connection_and_its_recorders_register_again() {
        string?         bearer    = "first";
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config, () => bearer), TimeProvider.System, _ => { });
        var             back      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Reconnected += () => {
            back.TrySetResult();

            return Task.CompletedTask;
        };

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");

        bearer = "second";
        transport.Restart();

        await back.Task.WithinDeadlineAsync("the connection to come back");
        await Assert.That(transport.IsConnected).IsTrue();
        await Assert.That(hub.Bearers).IsEquivalentTo(["Bearer first", "Bearer second"]);
    }

    /// <summary>Signed out, a source stays with its recorder and nothing leaves. Signing in again picks it all up.</summary>
    [Test]
    public async Task A_sign_out_stops_the_connection_until_a_sign_in() {
        string?         bearer    = "first";
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config, () => bearer), TimeProvider.System, _ => { });
        var             back      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.RequiresBearer = true;
        transport.Reconnected += () => {
            back.TrySetResult();

            return Task.CompletedTask;
        };

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");

        bearer = null;
        transport.Restart();

        await hub.WaitForAsync(h => h.OpenConnections == 0, "the connection to stop");
        await Assert.That(transport.IsConnected).IsFalse();

        bearer = "second";

        await back.Task.WithinDeadlineAsync("the connection to come back");
        await Assert.That(hub.Bearers).IsEquivalentTo(["Bearer first", "Bearer second"]);
    }

    [Test]
    public async Task A_restart_with_no_connection_starts_nothing() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);

        transport.Restart();

        await Assert.That(hub.Bearers).IsEmpty();
        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the first connect");
        await Assert.That(hub.Bearers.Count).IsEqualTo(1);
    }

    /// <summary>Hub calls on one connection aren't ordered: a detach landing behind the key's next connect would remove
    /// the new registration, and that source would record with no stop path.</summary>
    [Test]
    public async Task A_detach_finishes_before_that_key_connects_again() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.DetachHold = () => release.Task;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the first connect");

        var detach  = transport.DetachAsync(A, CancellationToken.None);
        await hub.WaitForAsync(h => h.Calls.Contains($"detach {SessionA}"), "the detach to reach the server");
        var connect = transport.ConnectAsync(A, CancellationToken.None);

        // Another key's connect goes straight past, so this one has been given every chance to overtake.
        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("another key's connect");
        await Assert.That(hub.Calls.Count(call => call == $"connect {SessionA}")).IsEqualTo(1);

        release.SetResult();
        await Task.WhenAll(detach, connect).WithinDeadlineAsync("the detach and the reconnect");

        await Assert.That(hub.Calls.ToList().LastIndexOf($"connect {SessionA}")).IsGreaterThan(hub.Calls.ToList().IndexOf($"detach {SessionA}"));
    }

    /// <summary>The token ends the host's wait. The connect behind it still has to wait for the server's answer, or a
    /// detach the host gave up on can land behind it.</summary>
    [Test]
    public async Task A_detach_given_up_on_still_holds_back_that_keys_connect() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var       giveUp    = new CancellationTokenSource();
        hub.DetachHold = () => release.Task;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the first connect");
        var detach = transport.DetachAsync(A, giveUp.Token);
        await hub.WaitForAsync(h => h.Calls.Contains($"detach {SessionA}"), "the detach to reach the server");

        await giveUp.CancelAsync();
        await Assert.That(async () => await detach).Throws<OperationCanceledException>();

        var connect = transport.ConnectAsync(A, CancellationToken.None);
        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("another key's connect");
        await Assert.That(hub.Calls.Count(call => call == $"connect {SessionA}")).IsEqualTo(1);

        release.SetResult();
        await connect.WithinDeadlineAsync("the reconnect");
    }

    /// <summary>A connect the host gave up on stays open until the server answers, so a detach behind it knows nothing
    /// is left to land.</summary>
    [Test]
    public async Task A_connect_given_up_on_stays_open_until_the_server_answers() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             landed    = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var       abandon   = new CancellationTokenSource();
        hub.ConnectHold = _ => landed.Task;
        hub.ResumeFrom  = _ => 7;

        var connect = transport.ConnectAsync(A, abandon.Token);
        await hub.WaitForAsync(h => h.Calls.Contains($"connect {SessionA}"), "the connect to reach the server");

        await abandon.CancelAsync();
        landed.SetResult();

        await Assert.That(await connect.WithinDeadlineAsync("the server's answer")).IsEqualTo(7);
    }

    [Test]
    public async Task A_token_that_fires_first_sends_nothing() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("the connection");

        await Assert.That(async () => await transport.ConnectAsync(A, new CancellationToken(canceled: true))).Throws<OperationCanceledException>();

        await Assert.That(hub.Calls).IsEquivalentTo([$"connect {SessionB}"]);
    }

    /// <summary>A stop already on its way during a detach can still arrive, and nobody is left to answer it.</summary>
    [Test]
    public async Task A_stop_for_a_key_no_longer_held_is_ignored() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             stops     = new StopLog(transport);

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("A's connect");
        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("B's connect");
        await transport.DetachAsync(A, CancellationToken.None).WithinDeadlineAsync("A's detach");

        await hub.PushStopSourceAsync(SessionA);
        await hub.PushStopSourceAsync(SessionB, "marker");
        await stops.Heard.Task.WithinDeadlineAsync("the stop behind it");

        await Assert.That(stops.Keys).IsEquivalentTo([SessionB]);
    }

    /// <summary>The server's key is dashless for GUIDs on both parts, and a recorder knows its source by its own key.</summary>
    [Test]
    public async Task A_stop_names_its_key_in_canonical_form() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             stops     = new StopLog(transport);
        var             session   = Guid.NewGuid();
        var             agent     = Guid.NewGuid();
        var             source    = new RecordingSource(session.ToString("D"), agent.ToString("D"), "/t.jsonl", "claude", null);

        await transport.ConnectAsync(source, CancellationToken.None).WithinDeadlineAsync("the connect");
        await hub.PushStopSourceAsync($"{session:N}-{agent:N}");
        await stops.Heard.Task.WithinDeadlineAsync("the stop");

        await Assert.That(stops.Keys).IsEquivalentTo([source.Key]);
    }

    /// <summary>A push with no key can't say which source it means, so on a shared connection it means none of them.</summary>
    [Test]
    public async Task A_stop_with_no_key_stops_nothing() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             stops     = new StopLog(transport);

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        await hub.PushStopWatcherAsync();
        await hub.PushStopSourceAsync(SessionA, "marker");
        await stops.Heard.Task.WithinDeadlineAsync("the stop behind it");

        await Assert.That(stops.Reasons).IsEquivalentTo(["marker"]);
    }

    [Test]
    public async Task A_stuck_handler_does_not_stall_another_sources_reconnect() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             stuck     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             reached   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        transport.Reconnected += () => {
            reached.TrySetResult();

            return stuck.Task;
        };
        transport.Reconnected += () => throw new InvalidOperationException("this one fails");

        var last = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Reconnected += () => {
            last.TrySetResult();

            return Task.CompletedTask;
        };

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        hub.BreakTransports();

        await last.Task.WithinDeadlineAsync("the handler after the stuck one");
        await reached.Task.WithinDeadlineAsync("the stuck handler");
        stuck.SetResult();
    }

    /// <summary>A clean close ends a watcher, because its session is over. This connection serves every source, so it
    /// comes back, and each recorder re-registers.</summary>
    [Test]
    public async Task A_clean_close_from_the_server_restarts_the_connection() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             back      = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        transport.Reconnected += () => {
            back.TrySetResult();

            return Task.CompletedTask;
        };

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        await hub.CloseCleanlyAsync();

        await back.Task.WithinDeadlineAsync("the connection to come back");
        await Assert.That(transport.IsConnected).IsTrue();
        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the re-registration");
    }

    sealed class StopLog {
        readonly Lock _gate = new();
        readonly List<(string Key, string Reason)> _stops = [];

        public StopLog(ServerRecorderTransport transport) =>
            transport.StopRequested += (key, reason) => {
                lock (_gate) _stops.Add((key, reason));
                Heard.TrySetResult();

                return Task.CompletedTask;
            };

        public TaskCompletionSource Heard { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public IReadOnlyList<string> Keys {
            get {
                lock (_gate) return [.. _stops.Select(stop => stop.Key)];
            }
        }

        public IReadOnlyList<string> Reasons {
            get {
                lock (_gate) return [.. _stops.Select(stop => stop.Reason)];
            }
        }
    }
}
