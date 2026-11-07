using System.Collections.Concurrent;
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

    /// <summary>A source beats its claim between sends, so a send using its whole budget mustn't reach the grace: a hook
    /// from outside could then take a source that is recording.</summary>
    [Test]
    public async Task A_send_gives_up_ahead_of_its_claim_going_stale() =>
        await Assert.That(ServerRecorderTransport.SendBudget < RecorderClaims.Grace).IsTrue();

    /// <summary>The send failed because the connection went, and a log saying the server was slow would send whoever reads
    /// it the wrong way. It still has to read as a failed send, not a stop.</summary>
    [Test]
    public async Task A_connection_closing_under_a_batch_says_so() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.BatchHold = line => line == 3 ? release.Task : null;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");

        var stuck = transport.SendBatchAsync(Batch(SessionA, 3), CancellationToken.None);
        await hub.WaitForAsync(h => h.Calls.Contains("batch 3"), "the batch to reach the server");
        await hub.CloseCleanlyAsync();

        Exception? failure = null;

        try {
            await stuck.WithinDeadlineAsync("the send");
        } catch (Exception ex) {
            failure = ex;
        }

        await Assert.That(failure).IsTypeOf<IOException>();
        await Assert.That(failure!.Message).Contains("closed");
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

    /// <summary>Each new connection would have every recorder re-register, so a server closing them as they open would get
    /// a tight loop and a storm of those. A start doesn't earn a quick retry: staying up does.</summary>
    [Test]
    public async Task A_server_closing_every_new_connection_is_backed_off_from() {
        var             time      = new TimerLog(DateTimeOffset.UtcNow);
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config), time, _ => { });
        hub.ClosesNewConnections = true;

        _ = transport.ConnectAsync(A, CancellationToken.None);

        await hub.WaitForAsync(h => h.Bearers.Count == 1, "the first connection");
        await WaitHarness.PollUntilAsync(() => time.Due.Contains(TimeSpan.FromSeconds(1)));
        await Assert.That(hub.Bearers.Count).IsEqualTo(1);

        time.Advance(TimeSpan.FromSeconds(1));
        await hub.WaitForAsync(h => h.Bearers.Count == 2, "the second connection");
        await WaitHarness.PollUntilAsync(() => time.Due.Contains(TimeSpan.FromSeconds(2)));
        await Assert.That(hub.Bearers.Count).IsEqualTo(2);

        time.Advance(TimeSpan.FromSeconds(2));
        await hub.WaitForAsync(h => h.Bearers.Count == 3, "the third connection");
        await WaitHarness.PollUntilAsync(() => time.Due.Contains(TimeSpan.FromSeconds(4)));
    }

    [Test]
    public async Task A_connection_that_stayed_up_comes_back_at_once() {
        var             time      = new TimerLog(DateTimeOffset.UtcNow);
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config), time, _ => { });

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");

        time.Advance(ServerRecorderTransport.MaxRestartDelay);
        await hub.CloseCleanlyAsync();

        await hub.WaitForAsync(h => h.Bearers.Count == 2, "the connection to come back");
    }

    /// <summary>SignalR's stop can hang on a wedged transport. The loop that starts the connection for the next source
    /// can't wait for it, so a source would sit with no connection.</summary>
    [Test]
    public async Task A_stop_that_hangs_holds_up_nothing() {
        var             time      = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var             logged    = new ConcurrentQueue<string>();
        var             stopping  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             hung      = new TaskCompletionSource();
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config), time, logged.Enqueue) {
            StopHub = () => {
                stopping.TrySetResult();

                return hung.Task;
            },
        };

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        await transport.DetachAsync(A, CancellationToken.None).WithinDeadlineAsync("the detach");
        await stopping.Task.WithinDeadlineAsync("the stop");

        await WaitHarness.PollUntilAsync(() => {
            time.Advance(ServerRecorderTransport.StopCap);

            return logged.Any(line => line.Contains("Stopping"));
        });

        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("a source behind the hung stop");
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

    /// <summary>A connect can wait out a sign-out and wake after a hook took its source for a watcher. Registering then
    /// would move the watcher's session onto this connection, and the server's stop for it would never reach the watcher.</summary>
    [Test]
    public async Task A_connect_that_outwaits_its_claim_registers_nothing() {
        string?         bearer    = null;
        var             keeps     = true;
        var             time      = new TimerLog(DateTimeOffset.UtcNow);
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = new ServerRecorderTransport(hub.Connection(Config, () => bearer), time, _ => { }, _ => keeps);
        hub.RequiresBearer = true;

        var connect = transport.ConnectAsync(A, CancellationToken.None);
        await WaitHarness.PollUntilAsync(() => hub.Refusals > 0 && time.Due.Contains(TimeSpan.FromSeconds(1)));

        keeps  = false;
        bearer = "first";
        time.Advance(TimeSpan.FromSeconds(1));

        await Assert.That(async () => await connect.WithinDeadlineAsync("the connect")).Throws<IOException>();
        await Assert.That(hub.Bearers).IsEquivalentTo(["Bearer first"]);
        await Assert.That(hub.Calls).DoesNotContain($"connect {SessionA}");
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

    /// <summary>A recorder stuck where no token reaches wakes to this, so what it was about to send stays here.</summary>
    [Test]
    public async Task A_silenced_session_sends_nothing_more_and_the_others_go_on() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        await transport.ConnectAsync(B, CancellationToken.None).WithinDeadlineAsync("the connect");

        await Assert.That(await transport.SilenceAsync(SessionA).WithinDeadlineAsync("the silence")).IsTrue();

        await Assert.That(async () => await transport.SendBatchAsync(Batch(SessionA, 5), CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(async () => await transport.SendTitleAsync(SessionA, "a title", CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(async () => await transport.UpdateTitleAsync(SessionA, "a title", null, 0, 0, 0, 0, CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(async () => await transport.DrainCompleteAsync(A, CancellationToken.None)).Throws<InvalidOperationException>();
        await Assert.That(async () => await transport.ConnectAsync(A, CancellationToken.None)).Throws<InvalidOperationException>();

        await transport.SendBatchAsync(Batch(SessionB, 6), CancellationToken.None).WithinDeadlineAsync("the other session's batch");

        await Assert.That(hub.Calls).DoesNotContain("batch 5");
        await Assert.That(hub.Calls.Count(call => call == $"connect {SessionA}")).IsEqualTo(1);
        await Assert.That(hub.Calls).DoesNotContain($"drain {SessionA}");
        await Assert.That(hub.Calls).Contains("batch 6");
    }

    /// <summary>A subagent's batch names its parent session, and a dashed spelling is the same session.</summary>
    [Test]
    public async Task A_silence_covers_any_spelling_of_the_session() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        const string    dashed    = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");

        await transport.SilenceAsync(dashed).WithinDeadlineAsync("the silence");

        await Assert.That(async () => await transport.SendBatchAsync(Batch(SessionA, 5) with { AgentId = "sub-1" }, CancellationToken.None)).Throws<InvalidOperationException>();
    }

    /// <summary>A cancel ends the sender's wait and nothing on the server, and the silence is what has to know the
    /// difference: the batch hasn't landed until the hub says so.</summary>
    [Test]
    public async Task A_silence_waits_for_a_send_whose_caller_gave_up() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var       stop      = new CancellationTokenSource();
        hub.BatchHold = _ => release.Task;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        var send = transport.SendBatchAsync(Batch(SessionA, 1), stop.Token);
        await hub.WaitForAsync(h => h.Calls.Contains("batch 1"), "the batch to reach the server");
        await stop.CancelAsync();
        await Assert.That(async () => await send.WithinDeadlineAsync("the send")).Throws<OperationCanceledException>();

        var silence = transport.SilenceAsync(SessionA);

        await Assert.That(silence.IsCompleted).IsFalse();

        release.SetResult();

        await Assert.That(await silence.WithinDeadlineAsync("the silence")).IsTrue();
    }

    /// <summary>The connection went with the call, and the server may have had it by then: nobody can say it didn't land.</summary>
    [Test]
    public async Task A_send_that_went_with_the_connection_is_no_answer() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        hub.BatchHold = _ => release.Task;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        var send = transport.SendBatchAsync(Batch(SessionA, 1), CancellationToken.None);
        await hub.WaitForAsync(h => h.Calls.Contains("batch 1"), "the batch to reach the server");

        var silence = transport.SilenceAsync(SessionA);
        await hub.CloseCleanlyAsync();

        await Assert.That(await silence.WithinDeadlineAsync("the silence")).IsFalse();
        await Assert.That(async () => await send).Throws<Exception>();
        release.SetResult();
    }

    /// <summary>The recorder's own detach empties the connection, and stopping it there would fail the batch the silence
    /// is waiting to hear about.</summary>
    [Test]
    public async Task The_connection_stays_up_for_a_send_with_no_answer_yet() {
        await using var hub       = await RecordingHubHost.StartAsync();
        await using var transport = Transport(hub);
        var             release   = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var       stop      = new CancellationTokenSource();
        hub.BatchHold = _ => release.Task;

        await transport.ConnectAsync(A, CancellationToken.None).WithinDeadlineAsync("the connect");
        var send = transport.SendBatchAsync(Batch(SessionA, 1), stop.Token);
        await hub.WaitForAsync(h => h.Calls.Contains("batch 1"), "the batch to reach the server");
        await stop.CancelAsync();
        var silence = transport.SilenceAsync(SessionA);
        await transport.DetachAsync(A, CancellationToken.None).WithinDeadlineAsync("the detach");

        await Assert.That(transport.Wanted).IsTrue();

        release.SetResult();

        await Assert.That(await silence.WithinDeadlineAsync("the silence")).IsTrue();
        await Assert.That(async () => await send.WithinDeadlineAsync("the send")).Throws<OperationCanceledException>();
        await hub.WaitForAsync(h => h.OpenConnections == 0, "the connection to stop with its last call");
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

    sealed class TimerLog(DateTimeOffset start) : FakeTimeProvider(start) {
        readonly ConcurrentQueue<TimeSpan> _due = new();

        public IReadOnlyList<TimeSpan> Due => [.. _due];

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            _due.Enqueue(dueTime);

            return base.CreateTimer(callback, state, dueTime, period);
        }
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
