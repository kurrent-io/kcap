using Capacitor.Cli.Core.Setup.Intelligence;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Core.Tests.Unit.Setup.Intelligence;

/// <summary>The settings read and the preference write answer the settings, a server's refusal by its code, a server
/// without the routes as needing an update, and anything else as failed, never a throw</summary>
public class IntelligenceSettingsClientTests {
    const string ReadPath        = "/api/admin/intelligence";
    const string PreferencesPath = "/api/admin/intelligence/preferences";

    const string Settings =
        """{"mode":"local_agent","harness":"codex","vendor":null,"model":null,"import_outcomes":true,"import_outcomes_pinned":false,"backlog_session_touches_per_day":300,"backlog_limit_pinned":true,"resolving":["titles","outcomes"]}""";

    static WireMockServer Serve(string method, string path, int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(path).UsingMethod(method)).RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    [Test]
    public async Task A_read_answers_every_field() {
        using var server = Serve("GET", ReadPath, 200, Settings);
        using var http = new HttpClient();

        var outcome = await IntelligenceSettingsClient.ReadAsync(http, server.Url! + "/", CancellationToken.None);

        var settings = ((IntelligenceSettingsOutcome.Read)outcome).Settings;
        IReadOnlyList<string> unread = [];
        await Assert.That(settings with { Resolving = unread }).IsEqualTo(new IntelligenceSettings("local_agent", "codex", null, null, true, false, 300, true, unread));
        await Assert.That(settings.Resolving).IsEquivalentTo(["titles", "outcomes"]);
    }

    [Test]
    public async Task A_preference_write_is_snake_case_and_sends_an_unsaid_setting_as_null() {
        using var server = Serve("PUT", PreferencesPath, 200, Settings);
        using var http = new HttpClient();

        var outcome = await IntelligenceSettingsClient.UpdateAsync(http, server.Url!, new(false, null), CancellationToken.None);

        await Assert.That(server.LogEntries.Single().RequestMessage.Body)
            .IsEqualTo("""{"import_outcomes":false,"backlog_session_touches_per_day":null}""");
        await Assert.That(outcome).IsTypeOf<IntelligenceSettingsOutcome.Read>();
    }

    [Test]
    [Arguments(400, """{"error":"backlog_limit_pinned_above_store"}""", "backlog_limit_pinned_above_store")]
    [Arguments(404, "", IntelligenceSettingsClient.NeedsUpdate)]
    public async Task A_refusal_carries_its_code(int status, string body, string code) {
        using var server = Serve("PUT", PreferencesPath, status, body);
        using var http = new HttpClient();

        var outcome = await IntelligenceSettingsClient.UpdateAsync(http, server.Url!, new(null, 50), CancellationToken.None);

        await Assert.That(outcome).IsEqualTo<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Refused(code));
    }

    [Test]
    [Arguments(500, "boom", "HTTP 500")]
    [Arguments(400, "not json", "HTTP 400")]
    [Arguments(200, "not json", "unreadable")]
    [Arguments(200, "{}", "unreadable")]
    public async Task Anything_else_fails_with_a_reason(int status, string body, string reason) {
        using var server = Serve("GET", ReadPath, status, body);
        using var http = new HttpClient();

        var outcome = await IntelligenceSettingsClient.ReadAsync(http, server.Url!, CancellationToken.None);

        await Assert.That(((IntelligenceSettingsOutcome.Failed)outcome).Problem).Contains(reason);
    }

    [Test]
    public async Task An_unreachable_server_fails_rather_than_throws() {
        using var http = new HttpClient();
        var outcome = await IntelligenceSettingsClient.ReadAsync(http, "http://127.0.0.1:9", CancellationToken.None);

        await Assert.That(outcome).IsTypeOf<IntelligenceSettingsOutcome.Failed>();
    }
}
