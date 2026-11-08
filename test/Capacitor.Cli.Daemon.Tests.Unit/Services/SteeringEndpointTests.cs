using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text;
using Capacitor.Cli.Daemon.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>The steering socket over real Unix-domain connections: who can reach it, what it answers, and that no
/// client can wedge or fault it.</summary>
[ExcludeOn(OS.Windows)] // steering isn't offered on Windows
[UnsupportedOSPlatform("windows")]
public class SteeringEndpointTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    const string Name = "t";

    const UnixFileMode GroupOrOther =
        UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
        UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute;

    static readonly byte[] WellFormed = Ascii(
        "POST /attach HTTP/1.1\r\nHost: localhost\r\nContent-Type: application/json\r\nContent-Length: 2\r\n\r\n{}");

    CapturingLogger Log { get; } = new();

    string SocketPath => Daemons.Store.SteeringSocketPath(Name);

    SteeringEndpoint Endpoint(
            TimeSpan? deadline = null, int maxConnections = 16,
            IReadOnlyDictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>>? routes = null) =>
        new(SocketPath, routes ?? TestSteering.NoRoutes, TimeProvider.System, Log) {
            MaxHeaderBytes  = 1024,
            MaxBodyBytes    = 4096,
            RequestDeadline = deadline ?? TimeSpan.FromSeconds(10),
            MaxConnections  = maxConnections,
        };

    [Test]
    public async Task The_socket_is_owner_only() {
        var stateDir = Daemons.CreateDir(Name);
        File.SetUnixFileMode(stateDir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                     | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                     | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);

        await using var endpoint = Endpoint();

        await Assert.That(endpoint.TryBind(out var failure)).IsTrue();
        await Assert.That(failure).IsNull();
        await Assert.That(File.GetUnixFileMode(SocketPath) & GroupOrOther).IsEqualTo(UnixFileMode.None);
        await Assert.That(File.GetUnixFileMode(stateDir) & GroupOrOther).IsEqualTo(UnixFileMode.None);
    }

    [Test]
    public async Task The_socket_comes_and_goes_without_a_restart() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var endpoint = Endpoint();

        endpoint.TryBind(out _);
        await Assert.That(StatusOf(await RequestAsync(WellFormed, cts.Token))).IsEqualTo("404");

        await endpoint.UnbindAsync();
        await Assert.That(File.Exists(SocketPath)).IsFalse();
        await Assert.That(async () => await RequestAsync(WellFormed, cts.Token)).Throws<SocketException>();

        await Assert.That(endpoint.TryBind(out _)).IsTrue();
        await Assert.That(StatusOf(await RequestAsync(WellFormed, cts.Token))).IsEqualTo("404");
    }

    /// <summary>Each attack is refused with a status or dropped, a well-formed request is served while it is held and
    /// after it is gone, and nothing reaches the endpoint's own fault log.</summary>
    [Test]
    [Arguments("garbage")]
    [Arguments("oversized_header_block")]
    [Arguments("oversized_content_length")]
    [Arguments("overflowing_content_length")]
    [Arguments("malformed_content_length")]
    [Arguments("lying_content_length")]
    [Arguments("slow_client")]
    [Arguments("abrupt_close")]
    public async Task A_malformed_request_is_refused_without_harming_the_daemon(string attack) {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var endpoint = Endpoint(deadline: TimeSpan.FromSeconds(1));
        endpoint.TryBind(out _);

        (byte[] Bytes, bool Close, string? Status) bad = attack switch {
            "garbage"                    => (Garbage(), false, "400"),
            "oversized_header_block"     => (Ascii($"POST /attach HTTP/1.1\r\nX-Pad: {new string('a', 2048)}\r\n\r\n"), false, "431"),
            "oversized_content_length"   => (Ascii("POST /attach HTTP/1.1\r\nContent-Length: 4097\r\n\r\n"), false, "413"),
            "overflowing_content_length" => (Ascii("POST /attach HTTP/1.1\r\nContent-Length: 99999999999999999999\r\n\r\n"), false, "413"),
            "malformed_content_length"   => (Ascii("POST /attach HTTP/1.1\r\nContent-Length: 2, 2\r\n\r\n{}"), false, "400"),
            "lying_content_length"       => (Ascii("POST /attach HTTP/1.1\r\nContent-Length: 100\r\n\r\n{}"), false, null),
            "slow_client"                => (Ascii("POST /attach HTTP/1.1\r\nHost: loc"), false, null),
            "abrupt_close"               => (Ascii("POST /attach HTTP/1.1\r\nContent-Length: 10\r\n\r\n{\"a\""), true, null),
            _                            => throw new ArgumentOutOfRangeException(nameof(attack)),
        };

        using var attacker = await ConnectAsync(cts.Token);
        await SendAsync(attacker, bad.Bytes, cts.Token);
        if (bad.Close) attacker.Close();

        await Assert.That(StatusOf(await RequestAsync(WellFormed, cts.Token))).IsEqualTo("404");

        if (!bad.Close) await Assert.That(StatusOf(await ReadToEndAsync(attacker, cts.Token))).IsEqualTo(bad.Status);

        await Assert.That(StatusOf(await RequestAsync(WellFormed, cts.Token))).IsEqualTo("404");
        await Assert.That(endpoint.IsBound).IsTrue();
        await Assert.That(Log.Warnings).IsEmpty();
    }

    [Test]
    public async Task Connections_past_the_cap_are_dropped_until_one_frees() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await using var endpoint = Endpoint(maxConnections: 2);
        endpoint.TryBind(out _);

        var first  = await ConnectAsync(cts.Token);
        var second = await ConnectAsync(cts.Token);

        await Assert.That(StatusOf(await RequestAsync(WellFormed, cts.Token))).IsNull();

        first.Dispose();
        second.Dispose();

        // A slot frees once the endpoint reads the close, which it does on its own schedule.
        string? status;
        while ((status = StatusOf(await RequestAsync(WellFormed, cts.Token))) is null) await Task.Delay(20, cts.Token);
        await Assert.That(status).IsEqualTo("404");
    }

    /// <summary>The shape a route gets and gives back: a POST's body in, JSON out, on a connection that closes.</summary>
    [Test]
    public async Task A_route_answers_its_post_and_nothing_else() {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var routes = new Dictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>> {
            ["/echo"] = (request, _) => Task.FromResult(new SteeringHttpResponse(200, request.Body)),
            ["/boom"] = (_, _) => throw new InvalidOperationException("route fault"),
        };
        await using var endpoint = Endpoint(routes: routes);
        endpoint.TryBind(out _);

        var echoed = await RequestAsync(Ascii("POST /echo HTTP/1.1\r\ncontent-length: 7\r\n\r\n{\"a\":1}"), cts.Token);

        await Assert.That(StatusOf(echoed)).IsEqualTo("200");
        await Assert.That(echoed).Contains("Content-Type: application/json\r\n");
        await Assert.That(echoed).Contains("Content-Length: 7\r\n");
        await Assert.That(echoed).Contains("Connection: close\r\n");
        await Assert.That(echoed).EndsWith("\r\n\r\n{\"a\":1}");

        await Assert.That(StatusOf(await RequestAsync(Ascii("GET /echo HTTP/1.1\r\n\r\n"), cts.Token))).IsEqualTo("405");
        await Assert.That(StatusOf(await RequestAsync(Ascii("POST /other HTTP/1.1\r\n\r\n"), cts.Token))).IsEqualTo("404");

        await Assert.That(StatusOf(await RequestAsync(Ascii("POST /boom HTTP/1.1\r\n\r\n"), cts.Token))).IsEqualTo("500");
        await Assert.That(Log.Warnings).Count().IsEqualTo(1);
        await Assert.That(StatusOf(await RequestAsync(Ascii("POST /echo HTTP/1.1\r\n\r\n"), cts.Token))).IsEqualTo("200");
    }

    async Task<Socket> ConnectAsync(CancellationToken ct) {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);

        try {
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), ct);

            return socket;
        } catch {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>The answer, or nothing when the endpoint dropped the connection, which can close it before the request
    /// is even written.</summary>
    async Task<string> RequestAsync(byte[] request, CancellationToken ct) {
        using var socket = await ConnectAsync(ct);

        try {
            await SendAsync(socket, request, ct);
        } catch (IOException) { }

        return await ReadToEndAsync(socket, ct);
    }

    static async Task SendAsync(Socket socket, byte[] bytes, CancellationToken ct) {
        await using var stream = new NetworkStream(socket, ownsSocket: false);
        await stream.WriteAsync(bytes, ct);
    }

    /// <summary>Everything the endpoint wrote before it closed. A refusal can close on input it never read, which a
    /// peer may see as a reset once the answer is read.</summary>
    static async Task<string> ReadToEndAsync(Socket socket, CancellationToken ct) {
        using var received = new MemoryStream();
        var buffer = new byte[4096];

        try {
            while (await socket.ReceiveAsync(buffer, SocketFlags.None, ct) is var n and > 0) received.Write(buffer, 0, n);
        } catch (SocketException) { }

        return Encoding.Latin1.GetString(received.ToArray());
    }

    /// <summary>The status code, or null when the endpoint closed without answering.</summary>
    static string? StatusOf(string response) =>
        response.Length == 0 ? null : response.Split(' ', 3)[1];

    static byte[] Ascii(string text) => Encoding.ASCII.GetBytes(text);

    static byte[] Garbage() => [
        .. Enumerable.Range(0, 256).Select(i => (byte)i).Where(b => b is not ((byte)'\r' or (byte)'\n')),
        .. "\r\n\r\n"u8.ToArray()
    ];
}
