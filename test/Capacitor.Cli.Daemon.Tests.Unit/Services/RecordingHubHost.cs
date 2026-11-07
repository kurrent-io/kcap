using System.Collections.Concurrent;
using Capacitor.Cli.Core;
using Capacitor.Cli.Daemon.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>
/// A real Kestrel and SignalR server at <c>/hubs/sessions</c> with the calls a recording connection and a daemon's main
/// connection make, running a connection's calls side by side as the real server does. Every call is recorded with the
/// connection that made it, in the order the hub did it.
/// </summary>
public sealed class RecordingHubHost : IAsyncDisposable {
    static readonly TimeSpan PollGap = TimeSpan.FromMilliseconds(20);

    readonly Lock _gate = new();
    readonly List<string> _calls = [];
    readonly List<string> _bearers = [];
    readonly ConcurrentDictionary<string, HubCallerContext> _contexts = new();

    WebApplication _app = null!;

    RecordingHubHost() { }

    public string Url { get; private set; } = "";

    /// <summary>The position <c>WatcherConnect</c> answers with, from the 1-based count of connects so far.</summary>
    public Func<int, int> ResumeFrom { get; set; } = _ => 0;

    /// <summary>Awaited inside <c>WatcherConnect</c>, given the session id, once the call is recorded.</summary>
    public Func<string, Task?>? ConnectHold { get; set; }

    /// <summary>Awaited inside <c>WatcherDetach</c>, once the call is recorded.</summary>
    public Func<Task?>? DetachHold { get; set; }

    /// <summary>Awaited inside <c>SendTranscriptBatch2</c>, given the batch's first line, once the call is recorded.</summary>
    public Func<int?, Task?>? BatchHold { get; set; }

    /// <summary>Whether the hub closes every connection as it opens, as a server in trouble can.</summary>
    public bool ClosesNewConnections { get; set; }

    /// <summary>Whether the hub refuses a negotiate that carries no bearer, as a signed-in server does.</summary>
    public bool RequiresBearer { get; set; }

    /// <summary>What the hub did, in order: <c>connect {session}</c>, <c>detach {session}</c>, <c>drain {session}</c>,
    /// <c>batch {first line}</c>.</summary>
    public IReadOnlyList<string> Calls {
        get {
            lock (_gate) return [.. _calls];
        }
    }

    /// <summary>The ids of the connections that called <c>WatcherConnect</c>.</summary>
    public HashSet<string> RecordingConnections { get; } = [];

    /// <summary>The ids of the connections that called <c>DaemonConnect</c>.</summary>
    public HashSet<string> DaemonConnections { get; } = [];

    /// <summary>How many connections are open.</summary>
    public int OpenConnections => _contexts.Count;

    /// <summary>The bearer each connection presented, in the order they connected.</summary>
    public IReadOnlyList<string> Bearers {
        get {
            lock (_gate) return [.. _bearers];
        }
    }

    /// <param name="hasDetach">False for a server from before <c>WatcherDetach</c>.</param>
    public static async Task<RecordingHubHost> StartAsync(bool hasDetach = true) {
        var host    = new RecordingHubHost();
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddSingleton(host);
        builder.Services.AddSignalR(o => o.MaximumParallelInvocationsPerClient = 100)
            .AddJsonProtocol(o => ServerConnection.ConfigureHubPayload(o.PayloadSerializerOptions));

        host._app = builder.Build();
        host._app.Use(async (context, next) => {
            if (host.RequiresBearer && context.Request.Headers.Authorization.Count == 0) {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;

                return;
            }

            await next();
        });

        if (hasDetach) host._app.MapHub<SessionsHub>("/hubs/sessions");
        else host._app.MapHub<OlderSessionsHub>("/hubs/sessions");

        await host._app.StartAsync();
        host.Url = host._app.Urls.Single();

        return host;
    }

    /// <summary>A connection as the daemon builds it, with no credential unless it is given one.</summary>
    public HubConnection Connection(TempConfigRoot config, Func<string?>? bearer = null) => DaemonHub.Build(
        new DaemonConfig { Name = "test", ServerUrl = Url, ConfigRoot = config.Root, Profiles = Resolutions.None(config.Root) },
        () => Task.FromResult(bearer?.Invoke()), _ => { }, Microsoft.Extensions.Logging.Abstractions.NullLoggerFactory.Instance, TimeProvider.System);

    public Task PushStopSourceAsync(string key, string reason = "session_ended") =>
        Hub.Clients.All.SendAsync("StopSource", key, reason);

    public Task PushStopWatcherAsync(string reason = "session_ended") =>
        Hub.Clients.All.SendAsync("StopWatcher", reason);

    /// <summary>Ends every connection the way a server does on its own: a close no client reads as a failure, so SignalR's
    /// automatic reconnect does not run.</summary>
    public Task CloseCleanlyAsync() {
        foreach (var context in _contexts.Values) context.Abort();

        return Task.CompletedTask;
    }

    /// <summary>Ends every connection's transport, which a client reads as a failure and reconnects from.</summary>
    public void BreakTransports() {
        foreach (var context in _contexts.Values) context.GetHttpContext()?.Abort();
    }

    /// <summary>Polls to the shared deadline; the failure names <paramref name="step"/>.</summary>
    public async Task WaitForAsync(Func<RecordingHubHost, bool> condition, string step) {
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

    IHubContext<SessionsHub> Hub => _app.Services.GetRequiredService<IHubContext<SessionsHub>>();

    void Record(string call) {
        lock (_gate) _calls.Add(call);
    }

    sealed class OlderSessionsHub(RecordingHubHost host) : Hub {
        public void DaemonConnect(DaemonConnect connect) {
            lock (host._gate) host.DaemonConnections.Add(Context.ConnectionId);
        }
    }

    sealed class SessionsHub(RecordingHubHost host) : Hub {
        public override Task OnConnectedAsync() {
            host._contexts[Context.ConnectionId] = Context;

            lock (host._gate) host._bearers.Add(Context.GetHttpContext()?.Request.Headers.Authorization.ToString() ?? "");

            if (host.ClosesNewConnections) Context.Abort();

            return Task.CompletedTask;
        }

        public override Task OnDisconnectedAsync(Exception? exception) {
            host._contexts.TryRemove(Context.ConnectionId, out _);

            return Task.CompletedTask;
        }

        public void DaemonConnect(DaemonConnect connect) {
            lock (host._gate) host.DaemonConnections.Add(Context.ConnectionId);
        }

        public async Task<int> WatcherConnect(string sessionId, string? agentId) {
            int count;

            lock (host._gate) {
                host._calls.Add($"connect {sessionId}");
                host.RecordingConnections.Add(Context.ConnectionId);
                count = host._calls.Count(c => c.StartsWith("connect ", StringComparison.Ordinal));
            }

            if (host.ConnectHold?.Invoke(sessionId) is { } hold) await hold;

            return host.ResumeFrom(count);
        }

        public async Task WatcherDetach(string sessionId, string? agentId) {
            host.Record($"detach {sessionId}");

            if (host.DetachHold?.Invoke() is { } hold) await hold;
        }

        public void WatcherDrainComplete(string sessionId, string? agentId) => host.Record($"drain {sessionId}");

        public async Task SendTranscriptBatch2(TranscriptBatch batch) {
            host.Record($"batch {batch.LineNumbers?.FirstOrDefault()}");

            if (host.BatchHold?.Invoke(batch.LineNumbers?.FirstOrDefault()) is { } hold) await hold;
        }
    }
}
