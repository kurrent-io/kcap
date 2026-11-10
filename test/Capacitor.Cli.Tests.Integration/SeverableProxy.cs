using System.Net;
using System.Net.Sockets;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// A loopback TCP relay in front of a server whose connections a test can reset. A reset reaches the
/// client as a failed transport, which is not what a server closing a connection cleanly looks like.
/// </summary>
public sealed class SeverableProxy : IAsyncDisposable {
    readonly TcpListener                      _listener = new(IPAddress.Loopback, 0);
    readonly CancellationTokenSource          _stop     = new();
    readonly Lock                             _gate     = new();
    readonly List<TcpClient>                  _sockets  = [];
    readonly List<Task>                       _pumps    = [];
    readonly int                              _targetPort;
    Task                                      _accepting = Task.CompletedTask;

    SeverableProxy(int targetPort) => _targetPort = targetPort;

    public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public static SeverableProxy Start(string targetUrl) {
        var proxy = new SeverableProxy(new Uri(targetUrl).Port);
        proxy._listener.Start();
        proxy._accepting = proxy.AcceptAsync();

        return proxy;
    }

    /// <summary>Resets every open connection; new ones are accepted as before.</summary>
    public void Sever() {
        lock (_gate) {
            foreach (var socket in _sockets) Reset(socket);
            _sockets.Clear();
        }
    }

    public async ValueTask DisposeAsync() {
        await _stop.CancelAsync();
        _listener.Stop();
        Sever();

        await Task.WhenAll([_accepting, .. _pumps]);
        _stop.Dispose();
    }

    static void Reset(TcpClient socket) {
        try {
            socket.LingerState = new LingerOption(true, 0);
            socket.Close();
        } catch (ObjectDisposedException) { }
    }

    async Task AcceptAsync() {
        try {
            while (!_stop.IsCancellationRequested) {
                var client   = await _listener.AcceptTcpClientAsync(_stop.Token);
                var upstream = new TcpClient();
                await upstream.ConnectAsync(IPAddress.Loopback, _targetPort, _stop.Token);

                lock (_gate) {
                    _sockets.Add(client);
                    _sockets.Add(upstream);
                    _pumps.Add(PumpAsync(client, upstream));
                    _pumps.Add(PumpAsync(upstream, client));
                }
            }
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }

    async Task PumpAsync(TcpClient from, TcpClient to) {
        try {
            await from.GetStream().CopyToAsync(to.GetStream(), _stop.Token);
        } catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or IOException or InvalidOperationException) { }
    }
}
