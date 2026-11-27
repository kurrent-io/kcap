using System.Net;
using System.Net.Sockets;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A loopback port whose connections complete but are never answered, the way a host behind a
/// firewall that drops packets looks to a client: an https request to <see cref="HttpsUrl"/> hangs in
/// the TLS handshake until something times it out.
/// </summary>
public sealed class SilentHost : IDisposable {
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public SilentHost() => _listener.Start();

    public string HttpsUrl => $"https://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}";

    public void Dispose() => _listener.Stop();
}
