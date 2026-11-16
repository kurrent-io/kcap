using System.Web;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RoutedImportClientTests {
    [Test]
    [Arguments("a&b")]
    [Arguments("a#b")]
    [Arguments("a+b")]
    [Arguments("a b")]
    [Arguments("a&agentId=b #c+d")]
    public async Task The_watermark_probe_sends_the_agent_id_the_server_decodes_back(string agentId) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/s1/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":7}"""));
        using var http = new HttpClient();

        var line = await new RoutedImportClient(http, TimeProvider.System, server.Url!)
            .FetchServerLastLineAsync("s1", CancellationToken.None, agentId);

        await Assert.That(line).IsEqualTo(7);
        var query = HttpUtility.ParseQueryString(server.LogEntries.Single().RequestMessage.RawQuery!);
        await Assert.That(query.GetValues("agentId")).IsEquivalentTo(new[] { agentId });
    }
}
