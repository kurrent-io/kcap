using System.Net.Sockets;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

static class ControlSocket {
    /// <summary>Connects to a control socket, retrying a refusal: Bind publishes the socket file before
    /// Listen, and a connect in that gap is refused.</summary>
    public static async Task<NetworkStream> ConnectAsync(string sockPath, CancellationToken ct) {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (true) {
            var sock = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            try {
                await sock.ConnectAsync(new UnixDomainSocketEndPoint(sockPath), ct);
                return new NetworkStream(sock, ownsSocket: true);
            } catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionRefused) {
                sock.Dispose();
                if (DateTime.UtcNow >= deadline) throw;
                await Task.Delay(20, ct);
            } catch {
                sock.Dispose();
                throw;
            }
        }
    }
}
