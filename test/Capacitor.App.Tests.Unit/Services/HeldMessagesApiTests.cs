using Capacitor.App.Services;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit.Services;

/// The held-message routes against the real authenticated HTTP lane. Every status is a value: a 404 from the list is a
/// server without the routes (or an agent the viewer cannot see) and reads as nothing held.
public class HeldMessagesApiTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly Guid Dispatch = Guid.Parse("11111111-2222-3333-4444-555555555555");

    [Test]
    public async Task The_list_parses_messages_and_maps_its_statuses() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/agents/a1/held-messages").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody($$"""
                  {"messages":[{"dispatch_id":"{{Dispatch}}","sender_user_id":"u2","sender_name":"Bea","text":"run the tests",
                   "state":"held","outcome":null,"held_at":"2026-11-01T10:00:00Z",
                   "attachments":[{"attachment_id":"f1","file_name":"log.txt","content_type":"text/plain","size_bytes":12}]}]}
                  """));
        server.Given(Request.Create().WithPath("/api/agents/old/held-messages").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/api/agents/down/held-messages").UsingGet()).RespondWith(Response.Create().WithStatusCode(503));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var api = new HeldMessagesApi(http, profiles);

        var read = await api.ListAsync("a1", CancellationToken.None);
        var message = read.Messages!.Single();
        await Assert.That(message.DispatchId).IsEqualTo(Dispatch);
        await Assert.That(message.SenderName).IsEqualTo("Bea");
        await Assert.That(message.State).IsEqualTo("held");
        await Assert.That(message.Attachments.Single().FileName).IsEqualTo("log.txt");

        await Assert.That((await api.ListAsync("old", CancellationToken.None)).Messages).IsEmpty();
        await Assert.That((await api.ListAsync("down", CancellationToken.None)).Messages).IsNull();
    }

    [Test]
    [Arguments("approve", 200, """{"state":"dispatched"}""", HeldMessageDecisionKind.Decided)]
    [Arguments("reject", 409, """{"error":"already_decided","message":"That message was already approved, rejected, withdrawn or discarded."}""", HeldMessageDecisionKind.AlreadyDecided)]
    [Arguments("approve", 409, """{"error":"question_pending","message":"The agent is waiting on a question its sender can't answer."}""", HeldMessageDecisionKind.QuestionPending)]
    [Arguments("withdraw", 404, """{"error":"held_message_not_found","message":"No."}""", HeldMessageDecisionKind.NotFound)]
    [Arguments("approve", 403, """{"error":"not_owner","message":"Only the agent's owner can do that."}""", HeldMessageDecisionKind.NotOwner)]
    [Arguments("reject", 500, "", HeldMessageDecisionKind.Unreachable)]
    public async Task A_decision_maps_each_status_to_an_outcome(string route, int status, string body, HeldMessageDecisionKind kind) {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath($"/api/agents/a1/held-messages/{Dispatch}/{route}").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var decision = route switch { "approve" => HeldMessageDecision.Approve, "reject" => HeldMessageDecision.Reject, _ => HeldMessageDecision.Withdraw };

        var outcome = await new HeldMessagesApi(http, profiles).DecideAsync("a1", Dispatch, decision, CancellationToken.None);
        await Assert.That(outcome.Kind).IsEqualTo(kind);
        await Assert.That(outcome.Refusal is null).IsEqualTo(kind == HeldMessageDecisionKind.Decided);
        if (kind == HeldMessageDecisionKind.Decided) await Assert.That(outcome.State).IsEqualTo("dispatched");
        if (kind == HeldMessageDecisionKind.QuestionPending)
            await Assert.That(outcome.Refusal).IsEqualTo("The agent is waiting on a question its sender can't answer.");
    }

    [Test]
    public async Task Without_a_server_nothing_throws() {
        var api = new HeldMessagesApi(null, null);
        await Assert.That((await api.ListAsync("a1", CancellationToken.None)).Messages).IsNull();
        await Assert.That((await api.DecideAsync("a1", Dispatch, HeldMessageDecision.Approve, CancellationToken.None)).Kind)
            .IsEqualTo(HeldMessageDecisionKind.Unauthorized);
    }
}
