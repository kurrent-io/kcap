using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary>
/// A <c>localhost</c> connect reaches whichever loopback family is listening, so the race never trades the ::1 refusal
/// for a 127.0.0.1 one when a server listens only on ::1.
/// </summary>
public class LoopbackConnectTests {
    [Test]
    public async Task Localhost_reaches_a_server_listening_on_ipv4_loopback_only() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var stream = (NetworkStream)await LoopbackConnect.ConnectAsync(new DnsEndPoint("localhost", port), CancellationToken.None);
        using var accepted = await listener.AcceptSocketAsync();

        await Assert.That(((IPEndPoint)stream.Socket.RemoteEndPoint!).Address).IsEqualTo(IPAddress.Loopback);
    }

    [Test]
    public async Task Localhost_reaches_a_server_listening_on_ipv6_loopback_only() {
        Skip.When(!Socket.OSSupportsIPv6, "This host has no IPv6.");

        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);

        try {
            listener.Start();
        } catch (SocketException) {
            Skip.Test("This host cannot listen on ::1.");
        }

        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var stream = (NetworkStream)await LoopbackConnect.ConnectAsync(new DnsEndPoint("localhost", port), CancellationToken.None);
        using var accepted = await listener.AcceptSocketAsync();

        await Assert.That(((IPEndPoint)stream.Socket.RemoteEndPoint!).Address).IsEqualTo(IPAddress.IPv6Loopback);
    }

    [Test]
    public async Task Localhost_with_nothing_listening_fails_as_a_refused_connect() {
        int port;

        using (var probe = new TcpListener(IPAddress.Loopback, 0)) {
            probe.Start();
            port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
        }

        await Assert.That(async () => await LoopbackConnect.ConnectAsync(new DnsEndPoint("localhost", port), CancellationToken.None))
            .Throws<SocketException>();
    }

    [Test]
    public async Task A_localhost_hub_on_windows_negotiates_through_a_handler_keeping_signalrs_cookies() {
        Skip.When(!OperatingSystem.IsWindows(), "Only Windows swaps the handler.");

        var cookies = new CookieContainer();
        using var inner = new HttpClientHandler { CookieContainer = cookies, AllowAutoRedirect = false };

        var handler = LoopbackConnect.ForHub(new Uri("http://localhost:5000/hubs/sessions"), inner);

        await Assert.That(handler).IsTypeOf<SocketsHttpHandler>();
        var sockets = (SocketsHttpHandler)handler;
        await Assert.That(sockets.CookieContainer).IsSameReferenceAs(cookies);
        await Assert.That(sockets.AllowAutoRedirect).IsFalse();
        await Assert.That(sockets.ConnectCallback is not null).IsTrue();
    }

    [Test]
    public async Task A_remote_hub_keeps_signalrs_handler() {
        using var inner = new HttpClientHandler();

        await Assert.That(LoopbackConnect.ForHub(new Uri("https://kcap.example.com/hubs/sessions"), inner)).IsSameReferenceAs(inner);
        await Assert.That(LoopbackConnect.WebSocketInvoker(new Uri("wss://kcap.example.com/hubs/sessions"))).IsNull();
    }

    [Test]
    public async Task Another_host_connects_as_resolved() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;

        await using var stream = (NetworkStream)await LoopbackConnect.ConnectAsync(new DnsEndPoint("127.0.0.1", port), CancellationToken.None);
        using var accepted = await listener.AcceptSocketAsync();

        await Assert.That(stream.Socket.Connected).IsTrue();
    }
}
