using System.Net.Sockets;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>
/// A handler whose every connect is refused at once, surfacing as the same <see cref="HttpRequestException"/> a real
/// refusal does. Windows takes about two seconds to refuse a loopback port nothing listens on, per address tried.
/// </summary>
static class RefusingHandler {
    public static SocketsHttpHandler Create() => new() {
        ConnectCallback = (_, _) => throw new SocketException((int)SocketError.ConnectionRefused),
    };
}
