using System.Net;
using System.Net.Sockets;

namespace Capacitor.Tests.Helpers;

/// <summary>
/// A loopback port that refuses every connection for as long as this is held. The port is bound and
/// never listened on, so the kernel answers a connect with a reset while no other socket can take the
/// number: a port that was listened on and then closed is free for the next parallel test's server,
/// and a client aimed at it then gets that server's answer instead of a refusal.
/// </summary>
public sealed class RefusingPort : IDisposable {
    readonly Socket  _v4;
    readonly Socket? _v6;

    public RefusingPort() {
        for (var attempt = 0;; attempt++) {
            _v4 = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
            _v4.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            Port = ((IPEndPoint)_v4.LocalEndPoint!).Port;

            // Held on ::1 as well, because a client dialing "localhost" tries both families.
            if (TryHoldV6(Port, out _v6)) return;

            _v4.Dispose();
            if (attempt == 20) throw new InvalidOperationException("No loopback port was free on both address families.");
        }
    }

    public int Port { get; }

    public string Url => $"http://127.0.0.1:{Port}";

    public void Dispose() {
        _v4.Dispose();
        _v6?.Dispose();
    }

    /// <summary>False only when another socket already has the number on ::1; a host without IPv6 has nothing to hold.</summary>
    static bool TryHoldV6(int port, out Socket? held) {
        held = null;
        if (!Socket.OSSupportsIPv6) return true;

        var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Stream, ProtocolType.Tcp) { ExclusiveAddressUse = true };
        try {
            socket.Bind(new IPEndPoint(IPAddress.IPv6Loopback, port));
            held = socket;

            return true;
        } catch (SocketException e) {
            socket.Dispose();

            return e.SocketErrorCode != SocketError.AddressAlreadyInUse;
        }
    }
}
