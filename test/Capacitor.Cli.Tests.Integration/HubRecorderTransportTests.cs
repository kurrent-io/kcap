using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Transcripts;
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

    /// <summary>The payload options <c>kcap watch</c> builds, with nothing to fall back on, so a type the AOT build cannot
    /// serialize fails here as it would there.</summary>
    static HubConnection WatchConnection(WatcherHubHost hub) =>
        new HubConnectionBuilder()
            .WithUrl($"{hub.Url}/hubs/sessions")
            .AddJsonProtocol(options => {
                options.PayloadSerializerOptions.TypeInfoResolver     = CapacitorJsonContext.Default;
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower;
            })
            .Build();

    static CanonicalTranscriptBatch Canonical(int line) =>
        CanonicalRoute.For("claude", Source.SessionId, Source.AgentId, serverSupports: true)!.Encode(new TranscriptBatch {
            SessionId   = Source.SessionId,
            AgentId     = Source.AgentId,
            Vendor      = "claude",
            Lines       = ["""{"type":"user","uuid":"30000000-0000-4000-8000-000000000001","timestamp":"2026-11-16T08:00:00Z","message":{"role":"user","content":"hi"}}"""],
            LineNumbers = [line],
        }, DateTimeOffset.UtcNow);

    [Test]
    public async Task A_canonical_batch_reaches_the_hub_and_its_answer_is_read() {
        await using var hub        = await WatcherHubHost.StartAsync();
        await using var connection = WatchConnection(hub);
        var             transport  = Transport(connection, Source.Key);
        var             batch      = Canonical(4);
        hub.CanonicalFormat = 1;
        await transport.ConnectAsync(Source, CancellationToken.None);

        await Assert.That(await transport.SupportsCanonicalAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await transport.SendCanonicalBatchAsync(batch, CancellationToken.None)).IsEqualTo(new CanonicalSendResult(5, Unsupported: false));
        await Assert.That(hub.CanonicalBatches.Single().Events.Select(e => e.EventId)).IsEquivalentTo(batch.Events.Select(e => e.EventId));

        hub.RefusesCanonical = true;

        await Assert.That((await transport.SendCanonicalBatchAsync(batch, CancellationToken.None)).Unsupported).IsTrue();
    }

    /// <summary>An older server has no capability call, and a server on a newer format is one this client cannot feed.</summary>
    [Test]
    [Arguments(null)]
    [Arguments(2)]
    public async Task A_server_without_format_one_takes_no_canonical_batches(int? format) {
        await using var hub        = await WatcherHubHost.StartAsync();
        await using var connection = WatchConnection(hub);
        var             transport  = Transport(connection, Source.Key);
        hub.CanonicalFormat = format;
        await transport.ConnectAsync(Source, CancellationToken.None);

        await Assert.That(await transport.SupportsCanonicalAsync(CancellationToken.None)).IsFalse();

        await transport.SendBatchAsync(new TranscriptBatch { SessionId = Source.SessionId, Lines = ["{}"], LineNumbers = [0], Vendor = "claude" }, CancellationToken.None);
        await hub.WaitForAsync(h => h.Batches.Count == 1, "the raw batch after the answer");
    }

    /// <summary>What a transport on a connection that outlives its sources adds to the hub transport.</summary>
    sealed class DetachingTransport(HubRecorderTransport inner, HubConnection connection) : IRecorderTransport {
        public bool IsConnected => inner.IsConnected;

        public Task<int> ConnectAsync(RecordingSource source, CancellationToken ct) => inner.ConnectAsync(source, ct);

        public Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct) => inner.SendBatchAsync(batch, ct);

        public Task<bool> SupportsCanonicalAsync(CancellationToken ct) => inner.SupportsCanonicalAsync(ct);

        public Task<CanonicalSendResult> SendCanonicalBatchAsync(CanonicalTranscriptBatch batch, CancellationToken ct) => inner.SendCanonicalBatchAsync(batch, ct);

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
