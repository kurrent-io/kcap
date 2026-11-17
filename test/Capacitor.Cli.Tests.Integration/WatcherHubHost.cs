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
    readonly List<string>                                   _registrations = [];

    WebApplication _app = null!;
    volatile bool  _down;

    WatcherHubHost() { }

    public string Url { get; private set; } = "";

    /// <summary>The position <c>WatcherConnect</c> answers with, from the 1-based count of connects so far.</summary>
    public Func<int, int> ResumeFrom { get; set; } = _ => 0;

    /// <summary>Awaited inside <c>WatcherConnect</c>, after the connect is recorded, so a test can hold the server's
    /// answer while the client gives up. Given the 1-based count of connects so far; null holds nothing.</summary>
    public Func<int, Task?>? ConnectHold { get; set; }

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

    /// <summary>What <c>WatcherConnect</c> answered and <c>WatcherDetach</c> asked, in the order the server did them.</summary>
    public IReadOnlyList<string> Registrations {
        get {
            lock (_gate) return [.. _registrations];
        }
    }

    /// <summary>Every line number received, through either send method, in arrival order.</summary>
    public IReadOnlyList<int> ReceivedLineNumbers {
        get {
            lock (_gate) return [.. _received.SelectMany(r => r.Batch.LineNumbers ?? [])];
        }
    }

    /// <param name="down">Aborts every request until <see cref="ComeUp"/>, so a watcher's connects fail as they would
    /// against a server not yet up, on a port the hub already owns.</param>
    /// <param name="parallelInvocations">How many calls of one connection the hub runs at once. Above one, a detach can
    /// pass a connect still running, as it can on the real server.</param>
    public static async Task<WatcherHubHost> StartAsync(bool down = false, int parallelInvocations = 1) {
        var host    = new WatcherHubHost();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(host);
        builder.Services.AddSignalR(o => o.MaximumParallelInvocationsPerClient = parallelInvocations).AddJsonProtocol(o => o.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower);

        host._app = builder.Build();
        host._down = down;
        host._app.Use(async (context, next) => {
            if (host._down) {
                context.Abort();

                return;
            }

            await next(context);
        });
        host._app.MapHub<WatcherHub>("/hubs/sessions");
        await host._app.StartAsync();
        host.Url = host._app.Urls.Single();

        return host;
    }

    /// <summary>Starts answering requests, for a hub started down.</summary>
    public void ComeUp() => _down = false;

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
        public async Task<int> WatcherConnect(string sessionId, string? agentId) {
            int resumeFrom, count;

            lock (host._gate) {
                host._connects.Add((sessionId, agentId));
                count      = host._connects.Count;
                resumeFrom = host.ResumeFrom(count);
            }

            if (host.ConnectHold?.Invoke(count) is { } hold) await hold;

            lock (host._gate) host._registrations.Add($"registered {count}");

            return resumeFrom;
        }

        public void WatcherDetach(string sessionId, string? agentId) {
            lock (host._gate) host._registrations.Add("detached");
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
