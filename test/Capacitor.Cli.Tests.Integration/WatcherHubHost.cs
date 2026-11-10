using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Capacitor.Cli.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// A real Kestrel and SignalR server at <c>/hubs/sessions</c> exposing only the calls a watcher makes,
/// and recording each. It answers <c>WatcherConnect</c> with whatever <see cref="ResumeFrom"/> says, so a
/// test decides where the server claims to be.
/// </summary>
public sealed class WatcherHubHost : IAsyncDisposable {
    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(20);

    readonly Lock                                           _gate        = new();
    readonly List<(string SessionId, string? AgentId)>      _connects    = [];
    readonly List<(bool Acked, TranscriptBatch Batch)>      _received    = [];
    readonly List<(string SessionId, string? AgentId)>      _drains      = [];

    WebApplication _app = null!;

    WatcherHubHost() { }

    public string Url { get; private set; } = "";

    /// <summary>The position <c>WatcherConnect</c> answers with, from the 1-based count of connects so far.</summary>
    public Func<int, int> ResumeFrom { get; set; } = _ => 0;

    public IReadOnlyList<(string SessionId, string? AgentId)> Connects => Snapshot(_connects);

    /// <summary>The batches sent through <c>SendTranscriptBatch2</c>, in arrival order.</summary>
    public IReadOnlyList<TranscriptBatch> Batches {
        get { lock (_gate) return [.. _received.Where(r => !r.Acked).Select(r => r.Batch)]; }
    }

    /// <summary>The batches sent through <c>SendTranscriptBatchAcked</c>, in arrival order.</summary>
    public IReadOnlyList<TranscriptBatch> AckedBatches {
        get { lock (_gate) return [.. _received.Where(r => r.Acked).Select(r => r.Batch)]; }
    }

    public IReadOnlyList<(string SessionId, string? AgentId)> Drains => Snapshot(_drains);

    /// <summary>Every line number received, through either send method, in arrival order.</summary>
    public IReadOnlyList<int> ReceivedLineNumbers {
        get {
            lock (_gate) return [.. _received.SelectMany(r => r.Batch.LineNumbers ?? [])];
        }
    }

    /// <param name="port">Zero takes any free port; a number from <see cref="ReservePort"/> brings a hub up where a
    /// watcher is already looking.</param>
    public static async Task<WatcherHubHost> StartAsync(int port = 0) {
        var host    = new WatcherHubHost();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
        builder.Services.AddSingleton(host);
        builder.Services.AddSignalR().AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        host._app = builder.Build();
        host._app.MapHub<WatcherHub>("/hubs/sessions");
        await host._app.StartAsync();
        host.Url = host._app.Urls.Single();

        return host;
    }

    /// <summary>A loopback port nothing listens on yet.</summary>
    public static int ReservePort() {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();

        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Tells every connected watcher to stop, as the server does when a session ends.</summary>
    public Task PushStopAsync(string reason = "test") =>
        _app.Services.GetRequiredService<IHubContext<WatcherHub>>().Clients.All.SendAsync("StopWatcher", reason);

    /// <summary>Polls to the shared deadline; the failure names <paramref name="step"/>.</summary>
    public async Task WaitForAsync(Func<WatcherHubHost, bool> condition, string step) {
        using var deadline = new CancellationTokenSource(BoundedWait.Deadline);

        while (!condition(this)) {
            try {
                await Task.Delay(PollGap, deadline.Token);
            } catch (OperationCanceledException) {
                throw new TimeoutException($"Stalled waiting for {step}: not done within {BoundedWait.DeadlineSeconds}s.");
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await _app.StopAsync();
        await _app.DisposeAsync();
    }

    IReadOnlyList<(string SessionId, string? AgentId)> Snapshot(List<(string SessionId, string? AgentId)> list) {
        lock (_gate) return [.. list];
    }

    sealed class WatcherHub(WatcherHubHost host) : Hub {
        public int WatcherConnect(string sessionId, string? agentId) {
            lock (host._gate) {
                host._connects.Add((sessionId, agentId));

                return host.ResumeFrom(host._connects.Count);
            }
        }

        public void SendTranscriptBatch2(TranscriptBatch batch) {
            lock (host._gate) host._received.Add((false, batch));
        }

        public TranscriptBatchAck SendTranscriptBatchAcked(TranscriptBatch batch) {
            lock (host._gate) host._received.Add((true, batch));

            return new TranscriptBatchAck(batch.LineNumbers is { Length: > 0 } sent ? sent[^1] + 1 : 0);
        }

        public void WatcherDrainComplete(string sessionId, string? agentId) {
            lock (host._gate) host._drains.Add((sessionId, agentId));
        }
    }
}
