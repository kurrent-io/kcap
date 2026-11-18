using System.Net;
using System.Net.Sockets;
using Capacitor.Cli.Core.Auth;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Auth;

/// <summary>Only a 404 reads as a missing workspace; anything else that is not a success is an outage.</summary>
public class WorkspaceProbeTests {
    [Test]
    [Arguments(200, WorkspaceAnswer.Live)]
    [Arguments(404, WorkspaceAnswer.Gone)]
    [Arguments(500, WorkspaceAnswer.NoAnswer)]
    [Arguments(502, WorkspaceAnswer.NoAnswer)]
    public async Task Reads_the_auth_config_status(int status, WorkspaceAnswer expected) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/auth/config").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody("""{"provider":"WorkOS"}"""));
        using var client = new HttpClient();

        var answer = await WorkspaceProbe.AskAsync(client, server.Urls[0] + "/", TimeProvider.System);

        await Assert.That(answer).IsEqualTo(expected);
    }

    [Test]
    public async Task A_refused_connection_is_no_answer() {
        // Reserve the port without listening. Stopping a server releases its port, allowing a
        // parallel test's server to take it and answer this probe (including with a real 404).
        using var reserved = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        reserved.ExclusiveAddressUse = true;
        reserved.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var url = $"http://127.0.0.1:{((IPEndPoint)reserved.LocalEndPoint!).Port}";
        // macOS drops a SYN to a port bound without listening instead of refusing it, so the connect
        // would otherwise run to the OS's own timeout.
        using var handler = new SocketsHttpHandler { UseProxy = false, ConnectTimeout = TimeSpan.FromMilliseconds(500) };
        using var client = new HttpClient(handler);

        await Assert.That(await WorkspaceProbe.AskAsync(client, url, TimeProvider.System)).IsEqualTo(WorkspaceAnswer.NoAnswer);
    }

    /// <summary>A discovered origin comes from the server; a malformed one must not escape as an
    /// exception and abort discovery of the others.</summary>
    [Test]
    [Arguments("")]
    [Arguments("not a url")]
    [Arguments("/relative")]
    [Arguments("ftp://acme.kcap.ai")]
    public async Task An_unusable_origin_is_no_answer(string origin) {
        using var client = new HttpClient();

        await Assert.That(await WorkspaceProbe.AskAsync(client, origin, TimeProvider.System)).IsEqualTo(WorkspaceAnswer.NoAnswer);
    }
}
