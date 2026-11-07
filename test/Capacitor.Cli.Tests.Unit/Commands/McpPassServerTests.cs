using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpPassServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Url = "https://kcap.test";

    const string ApprovalText = "You're sharing this session with Sam. Remove access yourself from the session's sharing if you need to";

    static readonly PassSharingGrantDto SessionGrant = new("session", "s-1", "Sam can see this session", "send messages for you to approve");

    static McpPassServer Server(IPassesApi api, string url = Url) => new(api, url, TimeProvider.System);

    static string Request(int id, string method, JsonObject? parameters = null) {
        var request = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method };
        if (parameters is not null) request["params"] = parameters;

        return request.ToJsonString();
    }

    static JsonObject Call(string tool, JsonObject arguments) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = 9, ["method"] = "tools/call", ["params"] = new JsonObject { ["name"] = tool, ["arguments"] = arguments } };

    static JsonObject PassArguments() => new() {
        ["recipient_id"] = "github:2", ["kind"] = "look", ["item_kind"] = "session", ["item_id"] = "s-1", ["brief"] = "Have a look at the retry loop",
    };

    /// Initialize, then list the tools; the two answers in order.
    static async Task<(JsonObject Init, JsonArray Tools)> StartAsync(McpPassServer server) {
        var output = new StringWriter();
        await server.ServeAsync(new StringReader(string.Join('\n', Request(1, "initialize"), Request(2, "tools/list"))), output);

        var lines = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                          .Select(l => JsonNode.Parse(l)!.AsObject()).ToList();

        return (lines[0]["result"]!.AsObject(), lines[1]["result"]!["tools"]!.AsArray());
    }

    static string Text(string response) => JsonNode.Parse(response)!["result"]!["content"]![0]!["text"]!.GetValue<string>();

    static bool IsError(string response) => JsonNode.Parse(response)!["result"]!["isError"]?.GetValue<bool>() == true;

    /// <summary>A server that does not offer passes leaves the agent nothing to see: no tool, no instructions.</summary>
    [Test]
    public async Task SurfacesFalse_ListsNoToolsNoInstructions() {
        var (init, tools) = await StartAsync(Server(new FakePassesApi { Offered = false }));

        await Assert.That(init["serverInfo"]!["name"]!.GetValue<string>()).IsEqualTo("kcap-pass");
        await Assert.That(init.ContainsKey("instructions")).IsFalse();
        await Assert.That(tools.Count).IsEqualTo(0);
    }

    /// <summary>An older server has no surfaces route; its 404 reads as passes not offered. The stub records the read,
    /// so the empty list is the answer to it rather than a read never made.</summary>
    [Test]
    public async Task Surfaces404_ListsNoTools() {
        using var stub = WireMockServer.Start();
        stub.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/users/me/surfaces").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));

        var (_, tools) = await StartAsync(Server(Api(stub.Urls[0]), stub.Urls[0]));

        await Assert.That(stub.LogEntries.Count(e => e.RequestMessage.Path == "/api/users/me/surfaces")).IsEqualTo(1);
        await Assert.That(tools.Count).IsEqualTo(0);
    }

    /// <summary>The prompt names the approval text and each grant, and the human's accept is what the approve route
    /// receives, marked as answered in the agent.</summary>
    [Test]
    public async Task Pass_NeedsGrant_Elicits_PostsAnswer() {
        var api = new FakePassesApi { SendResult = FakePassesApi.AwaitingApproval(ApprovalText, SessionGrant) };
        JsonObject? asked = null;

        var response = await Server(api).HandleToolCallAsync(9, Call("pass", PassArguments()), (parameters, _) => {
            asked = parameters;

            return Task.FromResult<JsonObject?>(new JsonObject { ["action"] = "accept" });
        });

        var message = asked!["message"]!.GetValue<string>();
        await Assert.That(message).Contains(ApprovalText);
        await Assert.That(message).Contains("Sam can see this session (session s-1)");
        await Assert.That(message).Contains("send messages for you to approve");
        await Assert.That(api.Approved.ToArray()).IsEquivalentTo(new[] { ("p1", "agent") });
        await Assert.That(api.Declined).IsEmpty();
        await Assert.That(Text(response)).Contains("Shared and sent");
    }

    /// <summary>A harness that cannot prompt gets the way to approve in kcap, and nothing is answered for the human.</summary>
    [Test]
    public async Task Pass_NoElicitation_ReturnsWaitingWithLink() {
        var api = new FakePassesApi { SendResult = FakePassesApi.AwaitingApproval(ApprovalText, SessionGrant) };

        var response = await Server(api).HandleToolCallAsync(9, Call("pass", PassArguments()), elicit: null);

        await Assert.That(Text(response)).Contains("Waiting for your approval in kcap");
        await Assert.That(Text(response)).Contains($"{Url}/passes?tab=sent");
        await Assert.That(api.Approved).IsEmpty();
        await Assert.That(api.Declined).IsEmpty();
    }

    /// <summary>The server's 422 refusal reaches the agent with its reason and where the item's sharing is changed.</summary>
    [Test]
    public async Task Pass_AskOwner_RelaysReason() {
        using var stub = WireMockServer.Start();
        stub.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/passes").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.UnprocessableEntity).WithHeader("Content-Type", "application/json")
                                 .WithBody("""{"code":"ask_owner","reason":"Sam can't see this session. Ask Tony to share it","share_url":"/sessions/s-1"}"""));

        var response = await Server(Api(stub.Urls[0]), stub.Urls[0]).HandleToolCallAsync(9, Call("pass", PassArguments()), elicit: null);

        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("Sam can't see this session. Ask Tony to share it");
        await Assert.That(Text(response)).Contains("ask_owner");
        await Assert.That(Text(response)).Contains("/sessions/s-1");
    }

    /// <summary>Two people match the name: both come back, and the agent is told to ask rather than pick.</summary>
    [Test]
    public async Task FindColleague_Ambiguous_ReturnsCandidates() {
        var api = new FakePassesApi { Colleagues = [new("github:2", "Sam Jones", null), new("github:3", "Sam Lee", null)] };

        var result = JsonNode.Parse(Text(await Server(api).HandleToolCallAsync(9, Call("find_colleague", new JsonObject { ["name"] = "Sam" }), null)))!;

        await Assert.That(result["candidates"]!.AsArray().Select(c => c!["recipient_id"]!.GetValue<string>()).ToArray())
            .IsEquivalentTo(["github:2", "github:3"]);
        await Assert.That(result["next"]!.GetValue<string>()).Contains("ask which one they mean");
    }

    PassesApi Api(string url) =>
        new(new FixedCapacitorHttpClient(), new CapacitorServer(url, Config.Root, Resolutions.At(url, Config.Root)), TimeProvider.System);
}
