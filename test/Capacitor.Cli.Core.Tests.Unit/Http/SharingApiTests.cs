using System.Net;
using Capacitor.Cli.Core.Http;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Http;

/// <summary><see cref="SharingApi"/> against a stub server: that both opaque keys reach the route as single segments,
/// and that every refusal keeps the code and message the server gave it.</summary>
public class SharingApiTests : IDisposable {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    string Url => _server.Urls[0];

    public void Dispose() => _server.Stop();

    SharingApi Api() =>
        new(new FixedCapacitorHttpClient(), new CapacitorServer(Url, Config.Root, Resolutions.At(Url, Config.Root)), TimeProvider.System);

    const string SharingJson = """
        {
          "can_manage": true,
          "manage_refusal": null,
          "grants": [{
            "audience": { "key": "user:7", "name": "Ada", "kind": "user", "detail": "ada@example.com", "avatar_url": null, "inactive": false },
            "relation": { "relation": "owner", "label": "Owner" },
            "relations": [],
            "removable": false
          }, {
            "audience": { "key": "team:platform", "name": "Platform", "kind": "squad", "detail": null, "avatar_url": null, "inactive": true },
            "relation": { "relation": "viewer", "label": "Can view" },
            "relations": [{ "relation": "viewer", "label": "Can view" }, { "relation": "editor", "label": "Can edit" }],
            "removable": true
          }]
        }
        """;

    [Test]
    public async Task A_listing_binds_every_grant_and_its_rights() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK)
                                    .WithHeader("Content-Type", "application/json").WithBody(SharingJson));

        var found = await Assert.That(await Api().GetAsync("page:a1")).IsTypeOf<SharingRead<SharingDto>.Found>();
        var listed = found!.Value;

        await Assert.That(listed.CanManage).IsTrue();
        await Assert.That(listed.Grants.Count).IsEqualTo(2);
        await Assert.That(listed.Grants[0].Audience.Detail).IsEqualTo("ada@example.com");
        await Assert.That(listed.Grants[1].Audience.Kind).IsEqualTo("squad");
        await Assert.That(listed.Grants[1].Audience.Inactive).IsTrue();
        await Assert.That(listed.Grants[1].Relations.Select(r => r.Relation)).IsEquivalentTo(new[] { "viewer", "editor" });
        await Assert.That(listed.Grants[1].Removable).IsTrue();
    }

    [Test]
    public async Task A_key_nothing_answers_to_reads_as_not_found() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:gone").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));

        await Assert.That(await Api().GetAsync("page:gone")).IsTypeOf<SharingRead<SharingDto>.NotFound>();
    }

    [Test]
    public async Task Candidates_carry_the_query_and_the_default_relation() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/candidates").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.OK).WithHeader("Content-Type", "application/json").WithBody("""
                   {"candidates":[{"audience":{"key":"user:9","name":"Sam","kind":"user","detail":null,"avatar_url":null,"inactive":false},
                     "relations":[{"relation":"viewer","label":"Can view"}],"default_relation":"viewer"}]}
                   """));

        var found = await Assert.That(await Api().CandidatesAsync("page:a1", "Sam & co")).IsTypeOf<SharingRead<SharingCandidatesDto>.Found>();

        await Assert.That(found!.Value.Candidates.Single().DefaultRelation).IsEqualTo("viewer");
        await Assert.That(_server.LogEntries.Single().RequestMessage.Query!["query"].Single()).IsEqualTo("Sam & co");
    }

    /// <summary>The stub matches the decoded path, so a match proves each key went out escaped into one segment: left
    /// raw, the <c>#</c> would start a fragment and the <c>?</c> a query.</summary>
    [Test]
    public async Task A_share_sends_its_relation_to_the_audiences_own_segment() {
        _server.Given(Request.Create().WithPath("/api/sharing/page#1/grants/team?platform").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        await Assert.That(await Api().ShareAsync("page#1", "team?platform", "viewer")).IsTypeOf<SharingWriteResult.Applied>();
        await Assert.That(_server.LogEntries.Single().RequestMessage.Body).IsEqualTo("""{"relation":"viewer"}""");
    }

    [Test]
    public async Task An_unshare_answers_no_content() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/grants/user:9").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NoContent));

        await Assert.That(await Api().UnshareAsync("page:a1", "user:9")).IsTypeOf<SharingWriteResult.Applied>();
    }

    [Test]
    [Arguments(HttpStatusCode.Forbidden, "refused")]
    [Arguments(HttpStatusCode.Conflict, "changed")]
    [Arguments(HttpStatusCode.UnprocessableEntity, "locked")]
    [Arguments(HttpStatusCode.UnprocessableEntity, "a_code_from_a_newer_server")]
    public async Task A_refusal_keeps_the_servers_code_and_message(HttpStatusCode status, string code) {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/grants/user:9").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(status).WithHeader("Content-Type", "application/json")
                                    .WithBody($$"""{"code":"{{code}}","message":"Said by the server."}"""));

        var refused = await Assert.That(await Api().ShareAsync("page:a1", "user:9", "viewer")).IsTypeOf<SharingWriteResult.Refused>();

        await Assert.That(refused!.Error).IsEqualTo(new SharingErrorDto(code, "Said by the server."));
    }

    /// <summary>A body that is JSON but not the sharing error shape binds to empty fields, which a caller would print.</summary>
    [Test]
    public async Task A_refusal_in_another_shape_names_the_status() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/grants/user:9").UsingPut())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.BadRequest).WithHeader("Content-Type", "application/problem+json")
                                    .WithBody("""{"title":"Bad Request","status":400}"""));

        var refused = await Assert.That(await Api().ShareAsync("page:a1", "user:9", "viewer")).IsTypeOf<SharingWriteResult.Refused>();

        await Assert.That(refused!.Error).IsEqualTo(new SharingErrorDto("refused", "Server returned HTTP 400"));
    }

    /// <summary>A read the server refuses says why in its own words, not as a bare status.</summary>
    [Test]
    public async Task A_refused_read_carries_the_servers_message() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/candidates").UsingGet())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.Forbidden).WithHeader("Content-Type", "application/json")
                                    .WithBody("""{"code":"refused","message":"Only the owner shares this page."}"""));

        var thrown = await Assert.That(async () => await Api().CandidatesAsync("page:a1", "sam")).Throws<CapacitorApiException>();

        await Assert.That(thrown!.Message).IsEqualTo("Only the owner shares this page.");
        await Assert.That(thrown.Status).IsEqualTo(403);
    }

    [Test]
    public async Task A_write_to_a_hidden_object_is_not_found() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/grants/user:9").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.NotFound));

        await Assert.That(await Api().UnshareAsync("page:a1", "user:9")).IsTypeOf<SharingWriteResult.NotFound>();
    }

    [Test]
    public async Task A_server_catching_up_is_a_refusal_to_retry() {
        _server.Given(Request.Create().WithPath("/api/sharing/page:a1/grants/user:9").UsingDelete())
               .RespondWith(Response.Create().WithStatusCode(HttpStatusCode.ServiceUnavailable));

        var refused = await Assert.That(await Api().UnshareAsync("page:a1", "user:9")).IsTypeOf<SharingWriteResult.Refused>();

        await Assert.That(refused!.Error.Message).IsEqualTo(SharingApi.CatchingUpMessage);
    }

    /// <summary>Escaping leaves a dot segment as it is, and the URL would then name the route above the one meant.</summary>
    [Test]
    [Arguments(".", "user:9")]
    [Arguments("page:a1", "..")]
    [Arguments("", "user:9")]
    public async Task A_dot_segment_key_is_refused_before_any_request(string objectKey, string audienceKey) {
        await Assert.That(async () => await Api().UnshareAsync(objectKey, audienceKey)).Throws<ArgumentException>();
        await Assert.That(_server.LogEntries).IsEmpty();
    }
}
