using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>The transport over a real <see cref="HubConnection"/>, because the behaviour at stake is
/// <c>InvokeAsync</c>'s: given a token, it completes locally the moment that token fires, with the server's
/// <c>WatcherConnect</c> still running. No fake of the transport can show that, since the fault would be in the fake.</summary>
public class HubRecorderTransportTests {
    static readonly RecordingSource Source = new("session", "agent", "/t.jsonl", "claude", null);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    static HubConnection Connection(WatcherHubHost hub) =>
        new HubConnectionBuilder()
            .WithUrl($"{hub.Url}/hubs/sessions")
            .AddJsonProtocol(options => options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower)
            .Build();

    static HubRecorderTransport Transport(HubConnection connection, string key) => new(connection, key, () => { }, _ => { }, TimeProvider.System);

    /// <summary>A host detaches a connect it gave up on once this task completes. Completing on the token would send the
    /// detach ahead of the registration, which then stays for the life of the connection.</summary>
    [Test]
    public async Task A_connect_given_up_on_stays_open_until_the_server_answers() {
        await using var hub        = await WatcherHubHost.StartAsync();
        await using var connection = Connection(hub);
        var             landed     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             transport  = Transport(connection, Source.Key);
        using var       abandon    = new CancellationTokenSource();
        hub.ConnectHold = _ => landed.Task;
        hub.ResumeFrom  = _ => 7;

        var connect = transport.ConnectAsync(Source, abandon.Token);
        await hub.WaitForAsync(h => h.Connects.Count == 1, "the connect to reach the server");

        await abandon.CancelAsync();
        landed.SetResult();

        await Assert.That(await connect.WithinDeadlineAsync("the server's answer")).IsEqualTo(7);
        await Assert.That(transport.StoppedBeforeConnect).IsFalse();
    }

    /// <summary>A token that fires before the invocation leaves still stops it: nothing reaches the server to detach.</summary>
    [Test]
    public async Task A_connect_stopped_before_it_leaves_sends_nothing() {
        await using var hub        = await WatcherHubHost.StartAsync();
        await using var connection = Connection(hub);
        var             transport  = Transport(connection, Source.Key);
        using var       abandon    = new CancellationTokenSource();
        await abandon.CancelAsync();

        await Assert.That(async () => await transport.ConnectAsync(Source, abandon.Token)).Throws<OperationCanceledException>();

        await Assert.That(transport.StoppedBeforeConnect).IsTrue();
        await Assert.That(hub.Connects).IsEmpty();
    }

    /// <summary>The race itself, with a hub that runs a connection's calls side by side as the real server can: the
    /// finish stops the run while the server sits in its first <c>WatcherConnect</c>, the drain's own connect and the
    /// source's detach go past it, and only a detach after that connect answers can take the registration away.</summary>
    [Test]
    public async Task A_connect_left_behind_is_detached_behind_the_servers_answer() {
        await using var hub        = await WatcherHubHost.StartAsync(parallelInvocations: 4);
        await using var connection = Connection(hub);
        var             landed     = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var             source     = Source with { SessionId = Guid.NewGuid().ToString("N"), TranscriptPath = Tmp.CreateFile("t.jsonl", "{\"type\":\"user\"}\n") };
        var             transport  = new DetachingTransport(Transport(connection, source.Key), connection);
        hub.ConnectHold = count => count == 1 ? landed.Task : null;

        var host = new RecorderHost(transport, _ => new NoStamps(), (_, _) => new RecorderContext(
            new CapacitorServer(hub.Url, Config.Root, Resolutions.None(Config.Root)), new FakeRecorderHousekeeping(), TimeProvider.System, _ => { }, () => { }), TimeProvider.System);

        host.TryAdd(source);
        await hub.WaitForAsync(h => h.Connects.Count == 1, "the connect to reach the server");
        await host.FinishAsync(source.Key, CancellationToken.None).WithinDeadlineAsync("the finish");
        await hub.WaitForAsync(h => h.Registrations.Contains("detached"), "the source's own detach");

        landed.SetResult();

        await hub.WaitForAsync(h => h.Registrations.Count(r => r == "detached") == 2, "a detach behind the late registration");
        await Assert.That(hub.Registrations.ToList().IndexOf("registered 1")).IsLessThan(hub.Registrations.ToList().LastIndexOf("detached"));
    }

    /// <summary>What a transport on a connection that outlives its sources adds to the hub transport.</summary>
    sealed class DetachingTransport(HubRecorderTransport inner, HubConnection connection) : IRecorderTransport {
        public bool IsConnected => inner.IsConnected;

        public Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) => inner.ConnectAsync(source, ct);

        public Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct) => inner.SendBatchAsync(batch, ct);

        public Task SendTitleAsync(string sessionId, string title, CancellationToken ct) => inner.SendTitleAsync(sessionId, title, ct);

        public Task UpdateTitleAsync(
                string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct) =>
            inner.UpdateTitleAsync(sessionId, title, model, inputTokens, outputTokens, cacheReadTokens, cacheWriteTokens, ct);

        public Task DrainCompleteAsync(RecordingSource source, CancellationToken ct) => inner.DrainCompleteAsync(source, ct);

        public Task DetachAsync(RecordingSource source, CancellationToken ct) => connection.InvokeAsync("WatcherDetach", source.SessionId, source.AgentId, ct);

        public event Func<string, string, Task> StopRequested {
            add => inner.StopRequested += value;
            remove => inner.StopRequested -= value;
        }

        public event Func<Task> Reconnected {
            add => inner.Reconnected += value;
            remove => inner.Reconnected -= value;
        }
    }
}
