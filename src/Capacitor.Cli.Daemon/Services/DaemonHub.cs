using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.LocalEdition;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>How this daemon reaches the server's hub, for every connection it opens: the destination checks, the
/// bearer, the headers and the retries. What each connection does with its hub is its own.</summary>
internal static class DaemonHub {
    /// <summary>Follows redirects itself, so each hop is checked against <see cref="DaemonConfig.LocalRoot"/>, and the
    /// handler under it follows none. Reports the expiry and error code of a refused bearer, so a 401 can be told apart
    /// from a lapsed token without the server's log.</summary>
    public static DelegatingHandler Guarded(DaemonConfig config, HttpMessageHandler inner, TimeProvider time, Action<string> report) =>
        new LocalDestinationHandler(config.LocalRoot, config.ServerUrl, config.DestinationRefusal, followRedirects: true) {
            InnerHandler = new BearerRejectionReportHandler(time, report) { InnerHandler = NoRedirects(inner) },
        };

    /// <param name="bearer">Asked on every connect and reconnect, so a long outage ends on whatever the credential is
    /// by then.</param>
    /// <param name="reportRejection">Hears a bearer the hub's own negotiate or socket was refused.</param>
    public static HubConnection Build(
            DaemonConfig config, Func<Task<string?>> bearer, Action<string> reportRejection, ILoggerFactory loggerFactory, TimeProvider time) {
        var url     = $"{config.ServerUrl.TrimEnd('/')}/hubs/sessions";
        var refusal = config.DestinationRefusal;

        var hub = new HubConnectionBuilder()
            .WithUrl(
                url,
                options => {
                    options.AccessTokenProvider       = bearer;
                    options.HttpMessageHandlerFactory = inner => Guarded(config, LoopbackConnect.ForHub(new Uri(url), inner), time, reportRejection);
                    HttpClientExtensions.AddObservationHeaders(options.Headers, config.UpdateCheckOff);
                    options.WebSocketFactory = (context, ct) => {
                        // A reconnect is checked too: after a purge the port may belong to anything.
                        LocalDestination.Check(context.Uri, config.LocalRoot, config.ServerUrl, refusal);

                        return BearerRejectionReportHandler.ConnectWebSocketAsync(
                            context.Uri, context.Options.AccessTokenProvider, time, reportRejection, ct);
                    };
                }
            )
            .WithAutomaticReconnect(new RetryPolicy())
            // Left to its default the builder drops the framework's own logs, a failed argument binding among them,
            // and a daemon that drops every invocation looks healthy.
            .ConfigureLogging(b => b.Services.AddSingleton(loggerFactory))
            .AddJsonProtocol(options => ServerConnection.ConfigureHubPayload(options.PayloadSerializerOptions))
            .Build();

        // Half the default, so the socket stays warm through cloudflared. ServerTimeout stays at its 30s: a server
        // mid-rollout may still send at 15s, and it needs twice that.
        hub.KeepAliveInterval = TimeSpan.FromSeconds(7);

        return hub;
    }

    /// <summary>Gives the UI's broadcasts somewhere to go. The server joins every authenticated connection to its UI
    /// group on connect and takes it out only inside <c>DaemonConnect</c> or <c>WatcherConnect</c>, so in the window
    /// between, which recurs on every reconnect, these would each log a missing handler and a binding failure.</summary>
    public static void IgnoreUiBroadcasts(HubConnection hub) {
        static Task Sink() => Task.CompletedTask;

        hub.On("AgentInstancesChanged", Sink);
        hub.On("DaemonsChanged",        Sink);
        hub.On("WelcomeStateChanged",   Sink);
        hub.On("DaemonAccountsChanged", Sink);

        hub.On<JsonElement>("ActiveSessionAdded",   _ => Sink());
        hub.On<JsonElement>("ActiveSessionChanged", _ => Sink());
        hub.On<JsonElement>("ActiveSessionRemoved", _ => Sink());
        hub.On<JsonElement>("SessionDeleted",       _ => Sink());

        hub.On<JsonElement, JsonElement>("LaunchFailed",        (_, _) => Sink());
        hub.On<JsonElement, JsonElement>("PermissionResponded", (_, _) => Sink());

        hub.On<JsonElement, JsonElement, JsonElement, JsonElement, JsonElement>(
            "PermissionRequested", (_, _, _, _, _) => Sink());
    }

    static HttpMessageHandler NoRedirects(HttpMessageHandler handler) {
        switch (handler) {
            case HttpClientHandler h:  h.AllowAutoRedirect = false; break;
            case SocketsHttpHandler s: s.AllowAutoRedirect = false; break;
        }

        return handler;
    }

    sealed class RetryPolicy : IRetryPolicy {
        static readonly TimeSpan[] Delays = [
            TimeSpan.FromSeconds(1),
            TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(5),
            TimeSpan.FromSeconds(10),
            TimeSpan.FromSeconds(30)
        ];

        public TimeSpan? NextRetryDelay(RetryContext retryContext) => Delays[Math.Min(retryContext.PreviousRetryCount, Delays.Length - 1)];
    }
}
