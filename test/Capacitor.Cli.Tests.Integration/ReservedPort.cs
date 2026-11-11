using System.Net;
using System.Net.Sockets;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>A loopback port held by a socket that is bound but never listens: a connect to it is refused, and nothing
/// else on the machine can take it until <see cref="Release"/>.</summary>
public sealed class ReservedPort : IDisposable {
    readonly Socket _socket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);

    public ReservedPort() {
        _socket.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        Port = ((IPEndPoint)_socket.LocalEndPoint!).Port;
    }

    public int Port { get; }

    /// <summary>Lets the port go, for whatever binds it next to do so at once.</summary>
    public void Release() => _socket.Dispose();

    public void Dispose() => Release();
}
