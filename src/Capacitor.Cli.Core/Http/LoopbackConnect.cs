using System.Net;
using System.Net.Sockets;

namespace Capacitor.Cli.Core.Http;

/// <summary>
/// On Windows, connects a <c>localhost</c> URL by racing 127.0.0.1 against ::1. The resolver lists ::1 first, the local
/// server listens on 127.0.0.1 only, and Windows takes about two seconds to refuse a loopback port nothing listens on:
/// connecting in resolver order spends that on every new connection, out of a hook's or a session end's budget.
/// </summary>
public static class LoopbackConnect {
    public static SocketsHttpHandler Apply(SocketsHttpHandler handler) {
        if (OperatingSystem.IsWindows()) handler.ConnectCallback = (context, ct) => ConnectAsync(context.DnsEndPoint, ct);

        return handler;
    }

    /// <summary>
    /// SignalR builds its own <see cref="HttpClientHandler"/> for the negotiate request, and that cannot take a connect
    /// callback; for a <c>localhost</c> hub on Windows this hands back a <see cref="SocketsHttpHandler"/> with the same
    /// settings. A client certificate or a certificate callback keeps SignalR's handler, since neither maps one-to-one.
    /// </summary>
    public static HttpMessageHandler ForHub(Uri hubUrl, HttpMessageHandler inner) {
        if (!Applies(hubUrl)
         || inner is not HttpClientHandler h
         || h.ClientCertificateOptions != ClientCertificateOption.Manual
         || h.ClientCertificates.Count > 0
         || h.ServerCertificateCustomValidationCallback is not null) {
            return inner;
        }

        return Apply(new SocketsHttpHandler {
            AllowAutoRedirect        = h.AllowAutoRedirect,
            MaxAutomaticRedirections = h.MaxAutomaticRedirections,
            AutomaticDecompression   = h.AutomaticDecompression,
            UseCookies               = h.UseCookies,
            CookieContainer          = h.CookieContainer,
            UseProxy                 = h.UseProxy,
            Proxy                    = h.Proxy,
            PreAuthenticate          = h.PreAuthenticate,
            Credentials              = h.UseDefaultCredentials ? CredentialCache.DefaultCredentials : h.Credentials,
        });
    }

    /// <summary>The invoker a <c>ClientWebSocket</c> to <paramref name="uri"/> connects through, or null to let it connect on its own.</summary>
    public static HttpMessageInvoker? WebSocketInvoker(Uri uri) => Applies(uri) ? SharedInvoker : null;

    static HttpMessageInvoker SharedInvoker => field ??= new(Apply(new SocketsHttpHandler()));

    static bool Applies(Uri uri) => OperatingSystem.IsWindows() && uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase);

    internal static async ValueTask<Stream> ConnectAsync(DnsEndPoint endpoint, CancellationToken ct) {
        if (!endpoint.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) {
            return new NetworkStream(await ConnectSocketAsync(endpoint, ct), ownsSocket: true);
        }

        using var losers = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var attempts = new List<Task<Socket>> { ConnectSocketAsync(new IPEndPoint(IPAddress.Loopback, endpoint.Port), losers.Token) };
        if (Socket.OSSupportsIPv6) attempts.Add(ConnectSocketAsync(new IPEndPoint(IPAddress.IPv6Loopback, endpoint.Port), losers.Token));

        Exception? failure = null;

        while (attempts.Count > 0) {
            var finished = await Task.WhenAny(attempts);
            attempts.Remove(finished);

            if (finished.IsCompletedSuccessfully) {
                await losers.CancelAsync();
                foreach (var other in attempts) _ = other.ContinueWith(DisposeLate, TaskScheduler.Default);

                return new NetworkStream(finished.Result, ownsSocket: true);
            }

            failure ??= finished.Exception?.InnerException;
        }

        ct.ThrowIfCancellationRequested();

        throw failure ?? new SocketException((int)SocketError.ConnectionRefused);
    }

    static void DisposeLate(Task<Socket> attempt) {
        if (attempt.IsCompletedSuccessfully) attempt.Result.Dispose();
        else _ = attempt.Exception;
    }

    static async Task<Socket> ConnectSocketAsync(EndPoint endpoint, CancellationToken ct) {
        var socket = endpoint is IPEndPoint ip
            ? new Socket(ip.AddressFamily, SocketType.Stream, ProtocolType.Tcp)
            : new Socket(SocketType.Stream, ProtocolType.Tcp);

        socket.NoDelay = true;

        try {
            await socket.ConnectAsync(endpoint, ct);

            return socket;
        } catch {
            socket.Dispose();

            throw;
        }
    }
}
