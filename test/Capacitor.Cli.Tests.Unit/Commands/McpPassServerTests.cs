using System.Net;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Http;
using Microsoft.Extensions.Time.Testing;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class McpPassServerTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Url = "https://kcap.test";

    const string ApprovalText = "You're sharing this session with Sam. Remove access yourself from the session's sharing if you need to";

    static readonly PassSharingGrantDto SessionGrant = new("session", "s-1", "Sam can see this session", "send messages for you to approve");

    const string SendingSession = "0f8fad5bd9cb469fa165708677289a3c";

    static McpPassServer Server(IPassesApi api, string url = Url) => new(api, url, TimeProvider.System, () => SendingSession);

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

    /// Initialize, then list the tools; the two answers in order. <paramref name="whileStarting"/> runs once start-up is
    /// under way.
    static async Task<(JsonObject Init, JsonArray Tools)> StartAsync(McpPassServer server, Action? whileStarting = null) {
        var output  = new StringWriter();
        var serving = server.ServeAsync(new StringReader(string.Join('\n', Request(1, "initialize"), Request(2, "tools/list"))), output);
        whileStarting?.Invoke();
        await serving.WaitAsync(TimeSpan.FromSeconds(10));

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

    /// <summary>A server that never answers the offer check holds start-up only for the offer budget, well inside a
    /// client's start-up timeout; then initialize is answered with no tools.</summary>
    [Test]
    public async Task SurfacesNeverAnswer_InitializeAnsweredWithinOfferBudget() {
        var time = new FakeTimeProvider();

        var (init, tools) = await StartAsync(new McpPassServer(new FakePassesApi { OfferedHangs = true }, Url, time, () => SendingSession),
                                             () => time.Advance(McpPassServer.OfferedBudget));

        await Assert.That(init["serverInfo"]!["name"]!.GetValue<string>()).IsEqualTo("kcap-pass");
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

    /// <summary>The human accepted but the approve call timed out or failed, so it may have landed: the agent is told
    /// not to send again and where the pass can be checked, not handed an internal error.</summary>
    [Test]
    [Arguments("timed out")]
    [Arguments("failed")]
    public async Task Pass_ApproveCallLost_SaysCheckSentLinkNotSendAgain(string lost) {
        var time = new FakeTimeProvider();
        var api = new FakePassesApi {
            SendResult  = FakePassesApi.AwaitingApproval(ApprovalText, SessionGrant),
            AnswerHangs = lost == "timed out",
            AnswerFails = lost == "failed" ? new CapacitorApiException(null, "Cannot reach the server") : null
        };

        var call = new McpPassServer(api, Url, time, () => SendingSession).HandleToolCallAsync(
            9, Call("pass", PassArguments()), (_, _) => Task.FromResult<JsonObject?>(new JsonObject { ["action"] = "accept" }));
        time.Advance(McpPassServer.RequestBudget);
        var response = await call.WaitAsync(TimeSpan.FromSeconds(10));

        await Assert.That(api.Approved.Count).IsEqualTo(1);
        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("Do not pass or message again");
        await Assert.That(Text(response)).Contains($"{Url}/passes?tab=sent");
    }

    /// <summary>A send that fails may follow a pass the server stored, so the agent is told to check, not to send again.</summary>
    [Test]
    public async Task Pass_SendCallLost_SaysCheckSentLinkNotSendAgain() {
        var api = new FakePassesApi { SendFails = new CapacitorApiException(null, "Cannot reach the server") };

        var response = await Server(api).HandleToolCallAsync(9, Call("pass", PassArguments()), elicit: null);

        await Assert.That(IsError(response)).IsTrue();
        await Assert.That(Text(response)).Contains("Do not pass or message again");
        await Assert.That(Text(response)).Contains($"{Url}/passes?tab=sent");
    }

    /// <summary>Only the client's own timeout becomes a handled failure: the server's budget running out stays a
    /// cancellation, which is what lets it answer that the server did not answer in time.</summary>
    [Test]
    public async Task PassesApi_CallerCancels_StaysCancellation() {
        using var stub = WireMockServer.Start();
        stub.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/passes").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK).WithDelay(TimeSpan.FromSeconds(10)));
        using var budget = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));

        var body = new StartPassBody("look", "github:2", "session", "s-1", Brief: "Have a look");

        await Assert.ThrowsAsync<OperationCanceledException>(() => Api(stub.Urls[0]).SendAsync(body, budget.Token));
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

    static readonly DateTimeOffset SentAt = new(2026, 10, 30, 9, 0, 0, TimeSpan.Zero);

    static readonly DateTimeOffset FollowUpDue = SentAt.AddDays(2);

    static PassSendResult SentWithFollowUp() => new PassSendResult.Sent(new("p1", "sent", [], null, null, FollowUpDue));

    /// The pass's colleague is Jonny, learned through find_colleague as an agent would; the clock reads 30 Oct in UTC.
    static async Task<string> PassToJonnyAsync(FakePassesApi api, JsonObject arguments, McpPassServer.Elicit? elicit = null) {
        var time = new FakeTimeProvider(SentAt);
        time.SetLocalTimeZone(TimeZoneInfo.Utc);
        var server = new McpPassServer(api, Url, time, () => SendingSession);

        await server.HandleToolCallAsync(9, Call("find_colleague", new JsonObject { ["name"] = "Jonny" }), null);

        return await server.HandleToolCallAsync(9, Call("pass", arguments), elicit);
    }

    static JsonObject PageToJonny() => new() {
        ["recipient_id"] = "github:7", ["kind"] = "look", ["item_kind"] = "page", ["item_id"] = "pg-1", ["brief"] = "Check the chart",
    };

    /// <summary>A pass the server answers with a follow-up time, sent straight or once the human approves, declares one
    /// loose end on the calling session, due then, in the reminder's own words.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Pass_DeclaresLooseEndWithNotBefore(bool approved) {
        var api = new FakePassesApi {
            Colleagues   = [new("github:7", "Jonny", null)],
            SendResult   = approved ? FakePassesApi.AwaitingApproval(ApprovalText, SessionGrant) : SentWithFollowUp(),
            AnswerResult = new PassAnswerResult.Answered(new("sent", "sent", [], FollowUpDue, null))
        };

        var response = await PassToJonnyAsync(api, PageToJonny(), (_, _) => Task.FromResult<JsonObject?>(new JsonObject { ["action"] = "accept" }));

        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(api.Sent.Single().SendingSessionId).IsEqualTo(SendingSession);
        await Assert.That(api.FollowUps.ToArray()).IsEquivalentTo(new[] {
            new DeclareLooseEndBody("You passed a Page to Jonny on 30 Oct. If you haven't had feedback, follow up.", SendingSession, FollowUpDue)
        });
        await Assert.That(Text(response)).Contains("follow_up");
    }

    /// <summary>With the reminder turned off nothing is declared, even if a server names a follow-up time anyway.</summary>
    [Test]
    public async Task Pass_ReminderOff_DeclaresNothing() {
        var api = new FakePassesApi { Colleagues = [new("github:7", "Jonny", null)], SendResult = SentWithFollowUp() };
        var arguments = PageToJonny();
        arguments["reminder_off"] = true;

        var response = await PassToJonnyAsync(api, arguments);

        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(api.Sent.Single().ReminderOff == true).IsTrue();
        await Assert.That(api.FollowUps).IsEmpty();
    }

    /// <summary>A message carries no sending session and creates no reminder, even if a server names a follow-up time.</summary>
    [Test]
    public async Task Message_DeclaresNothing() {
        var api = new FakePassesApi { SendResult = SentWithFollowUp() };

        var response = await Server(api).HandleToolCallAsync(
            9, Call("message", new JsonObject { ["recipient_id"] = "github:2", ["text"] = "The chart is wrong" }), elicit: null);

        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(api.Sent.Single().SendingSessionId).IsNull();
        await Assert.That(api.FollowUps).IsEmpty();
    }

    /// <summary>The reminder failing to save leaves the pass a success, with the failure as a note.</summary>
    [Test]
    public async Task Pass_LooseEndFails_PassStillSent() {
        var api = new FakePassesApi {
            Colleagues = [new("github:7", "Jonny", null)], SendResult = SentWithFollowUp(),
            FollowUpFails = new CapacitorApiException(500, "Server error")
        };

        var response = await PassToJonnyAsync(api, PageToJonny());

        await Assert.That(api.FollowUps.Count).IsEqualTo(1);
        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(Text(response)).Contains("Sent.");
        await Assert.That(Text(response)).Contains("The reminder to follow up was not saved: Server error");
    }

    /// <summary>The server refuses a sending session the caller does not own before storing anything, so the pass is sent
    /// again without it and succeeds, with no reminder.</summary>
    [Test]
    public async Task Pass_SendingSessionRefused_SentWithoutIt() {
        var api = new FakePassesApi {
            FirstSendResults = new([new PassSendResult.Refused(new(McpPassServer.SendingSessionNotOwned, "sending_session_id must name a session you own"))])
        };

        var response = await Server(api).HandleToolCallAsync(9, Call("pass", PassArguments()), elicit: null);

        await Assert.That(IsError(response)).IsFalse();
        await Assert.That(api.Sent.Select(b => b.SendingSessionId).ToArray()).IsEquivalentTo(new[] { SendingSession, null });
        await Assert.That(api.FollowUps).IsEmpty();
    }

    /// <summary>The reminder reaches the loose-end route as its declare body, with the due time as not_before.</summary>
    [Test]
    public async Task PassesApi_DeclareFollowUp_PostsLooseEnd() {
        using var stub = WireMockServer.Start();
        stub.Given(WireMock.RequestBuilders.Request.Create().WithPath("/api/loose-ends/declare").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK).WithBody("""{"declaration_id":"d1","created":true}"""));

        await Api(stub.Urls[0]).DeclareFollowUpAsync(new DeclareLooseEndBody("Follow up the pass", SendingSession, FollowUpDue));

        var body = JsonNode.Parse(stub.LogEntries.Single().RequestMessage.Body!)!;
        await Assert.That(body["text"]!.GetValue<string>()).IsEqualTo("Follow up the pass");
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(SendingSession);
        await Assert.That(body["not_before"]!.GetValue<DateTimeOffset>()).IsEqualTo(FollowUpDue);
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
