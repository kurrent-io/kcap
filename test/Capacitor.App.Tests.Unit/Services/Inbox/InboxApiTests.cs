using Capacitor.App.Services.Inbox;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.App.Tests.Unit.Services.Inbox;

/// The inbox routes against the real authenticated HTTP lane, in the server's snake_case wire shape.
public class InboxApiTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static void Json(WireMockServer server, IRequestBuilder request, string body, int status = 200) =>
        server.Given(request).RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json").WithBody(body));

    [Test]
    public async Task The_inbox_page_parses_every_field_the_bell_renders() {
        using var server = WireMockServer.Start();
        Json(server, Request.Create().WithPath("/api/notifications").WithParam("limit", "50").UsingGet(), """
            {"items":[{"logical_id":"n1","type":"agent_prompt","version":3,"updated_at":"2026-11-25T10:00:00Z","is_unread":true,
              "title":"reviewer is waiting","chips":["Take over"],"code_pills":["a.cs"],"link_url":"/sessions/s1","status_line":"Waiting",
              "link_label":"Open session","link_external":false,"detail":null,"viewer_context":null,
              "headline":[{"text":"reviewer","style":"strong","url":null,"external":false},{"text":" PR","style":"plain","url":"https://github.com/o/r/pull/1","external":true}],
              "lines":[[{"text":"a.cs","style":"code","url":null,"external":false}]],"quote":"Run tests?",
              "buttons":[{"answer":"allow","label":"Allow","style":"primary","detail":"Runs it once"},{"answer":"deny","label":"Deny","style":"danger"}],
              "reply_label":"Reply"}],
             "next_cursor":null,"unread_count":7}
            """);
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var read = await new InboxApi(http, profiles).InboxAsync(50, CancellationToken.None);

        await Assert.That(read.Kind).IsEqualTo(InboxReadKind.Ready);
        await Assert.That(read.Value!.UnreadCount).IsEqualTo(7);
        var item = read.Value.Items.Single();
        await Assert.That(item.Version).IsEqualTo(3);
        await Assert.That(item.Headline![1].External).IsTrue();
        await Assert.That(item.Lines![0][0].Style).IsEqualTo("code");
        await Assert.That(item.Buttons!.Select(b => b.Style)).IsEquivalentTo(["primary", "danger"]);
        await Assert.That(item.Buttons![0].Detail).IsEqualTo("Runs it once");
        await Assert.That(item.ReplyLabel).IsEqualTo("Reply");
        await Assert.That(item.LinkLabel).IsEqualTo("Open session");
        await Assert.That(item.Chips.Single()).IsEqualTo("Take over");
    }

    [Test]
    public async Task Mutations_send_the_version_and_report_whether_the_server_took_them() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/notifications/n1/read").UsingPost().WithBody(new JsonPartialMatcher("""{"version":3}""")))
              .RespondWith(Response.Create().WithStatusCode(204));
        server.Given(Request.Create().WithPath("/api/notifications/n1/dismiss").UsingPost()).RespondWith(Response.Create().WithStatusCode(409));
        server.Given(Request.Create().WithPath("/api/notifications/read-all").UsingPost()).RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"marked":2}"""));
        server.Given(Request.Create().WithPath("/api/notifications/prefs/pass_received").UsingPut()
                  .WithBody(new JsonPartialMatcher("""{"enabled":true,"slack_enabled":false}""")))
              .RespondWith(Response.Create().WithStatusCode(204));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var api = new InboxApi(http, profiles);

        await Assert.That(await api.MarkReadAsync("n1", 3, CancellationToken.None)).IsTrue();
        await Assert.That(await api.DismissAsync("n1", 3, CancellationToken.None)).IsFalse();
        await Assert.That(await api.MarkAllReadAsync(CancellationToken.None)).IsTrue();
        await Assert.That(await api.PutPrefAsync("pass_received", true, false, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task An_answer_returns_the_servers_sentence_whether_or_not_it_was_taken() {
        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/notifications/n1/answer").UsingPost()
                  .WithBody(new JsonPartialMatcher("""{"answer":"reply","text":"on it"}""")))
              .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                  .WithBody("""{"accepted":false,"message":"This prompt has already been answered, or is no longer waiting"}"""));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;

        var read = await new InboxApi(http, profiles).AnswerAsync("n1", "reply", "on it", CancellationToken.None);

        await Assert.That(read.Value!.Accepted).IsFalse();
        await Assert.That(read.Value.Message).StartsWith("This prompt has already been answered");
    }

    [Test]
    public async Task Prefs_surfaces_and_pass_controls_parse_and_an_older_server_reads_as_unavailable() {
        using var server = WireMockServer.Start();
        Json(server, Request.Create().WithPath("/api/notifications/prefs").UsingGet(), """
            {"types":[{"type":"cli_update_available","display_name":"CLI update available","description":"d","enabled":true,
              "slack_enabled":false,"slack_capable":false,"retired":false}],
             "slack":{"tenant_configured":true,"binding":null,"delivery":"needs_reconnect"}}
            """);
        Json(server, Request.Create().WithPath("/api/users/me/surfaces").UsingGet(), """{"passes":true}""");
        Json(server, Request.Create().WithPath("/api/passes/mutes/u9").UsingDelete(),
            """{"muted_sender_ids":[],"muted_senders":[],"agent_opted_out":true}""");
        server.Given(Request.Create().WithPath("/api/notifications/unread-count").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        var (http, profiles, provider) = await WireMockLane.BuildAsync(server, Config.Root);
        await using var _ = provider;
        var api = new InboxApi(http, profiles);

        var prefs = (await api.PrefsAsync(CancellationToken.None)).Value!;
        await Assert.That(prefs.Slack.Delivery).IsEqualTo("needs_reconnect");
        await Assert.That(prefs.Types.Single().SlackCapable).IsFalse();
        var surfaces = (await api.SurfacesAsync(CancellationToken.None)).Value!;
        await Assert.That(surfaces.Passes).IsTrue();
        await Assert.That(surfaces.Notifications).IsFalse();
        await Assert.That((await api.UnmuteAsync("u9", CancellationToken.None)).Value!.AgentOptedOut).IsTrue();
        await Assert.That((await api.UnreadCountAsync(CancellationToken.None)).Kind).IsEqualTo(InboxReadKind.Unavailable);
    }

    [Test]
    public async Task Without_a_server_every_read_is_signed_out() {
        var api = new InboxApi(null, null);
        await Assert.That((await api.UnreadCountAsync(CancellationToken.None)).Kind).IsEqualTo(InboxReadKind.SignedOut);
        await Assert.That(await api.MarkAllReadAsync(CancellationToken.None)).IsFalse();
        await Assert.That(api.Resolve("/sessions/s1")).IsNull();
    }
}
