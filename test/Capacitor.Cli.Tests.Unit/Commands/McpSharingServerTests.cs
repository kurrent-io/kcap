using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><see cref="McpSharingServer"/> against a stub server: both opaque keys reach the route as single segments,
/// and a refusal reaches the agent in the server's words with its code.</summary>
public class McpSharingServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static JsonObject Args(string json) => JsonNode.Parse(json)!.AsObject();

    McpSharingServer Server() =>
        new(Config.Root, Resolutions.None(Config.Root), AuthFixtures.NewTokenStore(Config.Root), new FixedCapacitorHttpClient(),
            NoTelemetry.Startup, TimeProvider.System);

    static async Task<(bool IsError, string Text)> CallAsync(McpSharingServer server, string baseUrl, JsonObject arguments, string tool) {
        using var client = new HttpClient();

        var request = new JsonObject { ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } };
        var result  = JsonNode.Parse(await server.HandleToolCallAsync(JsonNode.Parse("1")!, request, client, baseUrl))!["result"]!;

        return (result["isError"]?.GetValue<bool>() ?? false, result["content"]![0]!["text"]!.GetValue<string>());
    }

    [Test]
    public async Task The_tool_list_is_exactly_these_four_and_both_writes_are_destructive() {
        var tools = McpSharingServer.BuildToolsList();

        await Assert.That(tools.Select(t => t.Name)).IsEquivalentTo(new[] {
            "list_sharing_grants", "search_sharing_candidates", "set_sharing_grant", "remove_sharing_grant"
        });
        await Assert.That(tools.Where(t => t.Annotations.ReadOnlyHint == true).Select(t => t.Name))
                    .IsEquivalentTo(new[] { "list_sharing_grants", "search_sharing_candidates" });
        await Assert.That(tools.Where(t => t.Annotations.DestructiveHint == true).Select(t => t.Name))
                    .IsEquivalentTo(new[] { "set_sharing_grant", "remove_sharing_grant" });
    }

    /// <summary>The stub matches the decoded path, so a match proves both keys went out escaped into one segment each.</summary>
    [Test]
    public async Task Set_sharing_grant_sends_the_relation_to_the_audiences_own_segment() {
        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/sharing/page#a1/grants/team?platform").UsingPut())
            .RespondWith(Response.Create().WithStatusCode(204));

        var (isError, text) = await CallAsync(Server(), stub.Url!,
            Args("""{"object_key":"page#a1","audience_key":"team?platform","relation":"viewer"}"""), "set_sharing_grant");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).Contains("team?platform now holds 'viewer'");
        await Assert.That(stub.LogEntries.Single().RequestMessage.Body).IsEqualTo("""{"relation":"viewer"}""");
    }

    [Test]
    public async Task List_sharing_grants_returns_the_servers_listing_behind_the_untrusted_notice() {
        const string listing = """{"can_manage":false,"manage_refusal":null,"grants":[]}""";

        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/sharing/page:a1").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody(listing));

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"object_key":"page:a1"}"""), "list_sharing_grants");

        await Assert.That(isError).IsFalse();
        await Assert.That(text).IsEqualTo(
            $$"""{"notice":{{System.Text.Json.JsonSerializer.Serialize(McpSharingServer.UntrustedNotice)}},"can_manage":false,"manage_refusal":null,"grants":[]}""");
    }

    [Test]
    public async Task Search_sharing_candidates_passes_the_query() {
        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/sharing/page:a1/candidates").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"candidates":[]}"""));

        await CallAsync(Server(), stub.Url!, Args("""{"object_key":"page:a1","query":"Sam & co"}"""), "search_sharing_candidates");

        await Assert.That(stub.LogEntries.Single().RequestMessage.Query!["query"].Single()).IsEqualTo("Sam & co");
    }

    /// <summary>The server's words, with its code, so an agent can tell a retry from a refusal; a 403 is the object's own rules
    /// saying no to someone who may see its grants.</summary>
    [Test]
    [Arguments(403, "refused")]
    [Arguments(409, "changed")]
    [Arguments(422, "relation_not_offered")]
    public async Task A_sharing_refusal_carries_the_servers_code_and_message(int status, string code) {
        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/sharing/page:a1/grants/user:9").UsingDelete())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody($$"""{"code":"{{code}}","message":"Said by the server."}"""));

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"object_key":"page:a1","audience_key":"user:9"}"""), "remove_sharing_grant");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo($"Error ({code}): Said by the server.");
    }

    [Test]
    public async Task A_sharing_call_on_a_hidden_object_says_where_the_key_comes_from() {
        using var stub = WireMockServer.Start();
        stub.Given(Request.Create().WithPath("/api/sharing/page:a1").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));

        var (isError, text) = await CallAsync(Server(), stub.Url!, Args("""{"object_key":"page:a1"}"""), "list_sharing_grants");

        await Assert.That(isError).IsTrue();
        await Assert.That(text).IsEqualTo(McpSharingServer.NoSuchObjectMessage);
    }

    [Test]
    [Arguments("""{"object_key":"page:a1","audience_key":"user:9"}""")]
    [Arguments("""{"audience_key":"user:9","relation":"viewer"}""")]
    [Arguments("""{"object_key":"..","audience_key":"user:9","relation":"viewer"}""")]
    public async Task A_grant_missing_an_argument_or_with_a_dot_key_sends_nothing(string arguments) {
        using var stub = WireMockServer.Start();

        var (isError, _) = await CallAsync(Server(), stub.Url!, Args(arguments), "set_sharing_grant");

        await Assert.That(isError).IsTrue();
        await Assert.That(stub.LogEntries).IsEmpty();
    }
}
