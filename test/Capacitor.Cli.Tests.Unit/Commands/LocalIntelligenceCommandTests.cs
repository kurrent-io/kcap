using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalEdition;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>The command posts to the local server and reports the outcome, as one JSON line under --json, with an
/// exit code a script can branch on</summary>
[NotInParallel]
public class LocalIntelligenceCommandTests {
    const string InstallationId = "0123456789abcdef0123456789abcdef";

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Applied =
        """{"mode":"local_agent","harness":"claude","vendor":null,"import_outcomes":true,"backlog_session_touches_per_day":200,"resolving":["titles","outcomes"]}""";

    LocalIntelligenceCommand Command(string? server, Func<string, string?>? env = null) =>
        new(new FixedCapacitorHttpClient(), new LocalRoot("/no-local-root"), Config.Root) {
            Installation = () => server is null ? null : new LocalInstallation(InstallationId, new Uri(server).Port, server, "1.0.0"),
            Variable     = env ?? (_ => null),
        };

    const string Settings =
        """{"mode":"provider","harness":null,"vendor":"openai","model":"gpt-5-mini","import_outcomes":false,"import_outcomes_pinned":true,"backlog_session_touches_per_day":500,"backlog_limit_pinned":false,"resolving":["titles"]}""";

    const string SettingsLine =
        """{"ok":true,"settings":{"mode":"provider","vendor":"openai","model":"gpt-5-mini","import_outcomes":false,"import_outcomes_pinned":true,"backlog_session_touches_per_day":500,"backlog_limit_pinned":false,"resolving":["titles"]}}""";

    static WireMockServer ServeSettings(string method, string path, int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath(path).UsingMethod(method))
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    static WireMockServer Serve(int status, string body) {
        var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/admin/intelligence").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(status).WithBody(body));

        return server;
    }

    [Test]
    public async Task Applied_prints_one_json_line_and_exits_zero() {
        using var server = Serve(200, Applied);
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", "claude", "--import-outcomes", "--json"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo(
            """{"ok":true,"mode":"local_agent","harness":"claude","import_outcomes":true,"backlog_session_touches_per_day":200,"resolving":["titles","outcomes"]}""");
        await Assert.That(server.LogEntries.Single().RequestMessage.Body).Contains("\"import_outcomes\":true");
    }

    [Test]
    public async Task A_refusal_prints_the_code_and_exits_two() {
        using var server = Serve(400, """{"error":"key_rejected: 401"}""");
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url, _ => "sk").HandleAsync(["local", "intelligence", "anthropic", "--json"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo("""{"ok":false,"error":"key_rejected: 401"}""");
    }

    [Test]
    public async Task The_key_is_read_from_the_vendors_variable_and_sent_in_the_body() {
        using var server = Serve(200, Applied);
        using var _ = ConsoleOutput.StartCapture();

        await Command(server.Url, name => name == "COHERE_API_KEY" ? "co-key" : null).HandleAsync(["local", "intelligence", "cohere", "--json"]);

        await Assert.That(server.LogEntries.Single().RequestMessage.Body).Contains("\"api_key\":\"co-key\"");
    }

    [Test]
    public async Task A_provider_without_its_key_under_json_is_refused_before_any_request() {
        using var server = Serve(200, Applied);
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", "openai", "--json"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(stdout.GetCapturedOutput()).Contains("OPENAI_API_KEY");
        await Assert.That(server.LogEntries).IsEmpty();
    }

    [Test]
    public async Task No_local_installation_fails() {
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(null).HandleAsync(["local", "intelligence", "none", "--json"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(stdout.GetCapturedOutput()).Contains("\"ok\":false");
    }

    [Test]
    public async Task An_unreachable_server_fails() {
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command("http://127.0.0.1:9").HandleAsync(["local", "intelligence", "none", "--json"]);

        await Assert.That(exit).IsEqualTo(1);
    }

    /// <summary>The applied mode is what decides whether this CLI calls a model for the installation's sessions, so
    /// none is recorded as surely as an answer that turns it on.</summary>
    [Test]
    [Arguments("local_agent", "claude")]
    [Arguments("none", "none")]
    public async Task An_applied_answer_records_the_mode_the_server_returned(string mode, string answer) {
        using var server = Serve(200, Applied.Replace("\"local_agent\"", $"\"{mode}\""));
        using var _ = ConsoleOutput.StartCapture();

        await Command(server.Url).HandleAsync(["local", "intelligence", answer, "--json"]);

        await Assert.That(LocalIntelligenceRecord.For(Config.Root, InstallationId)).IsEqualTo(new LocalIntelligenceRecord(InstallationId, mode));
    }

    /// <summary>An unsaved none reads the same as a saved one, so only a mode that runs a model warns.</summary>
    [Test]
    [Arguments("local_agent", "claude", true)]
    [Arguments("none", "none", false)]
    public async Task An_applied_answer_that_cannot_be_recorded_warns_only_when_it_runs_a_model(string mode, string answer, bool warns) {
        Directory.CreateDirectory(Config.Root.Path(LocalIntelligenceRecord.FileName));
        using var server = Serve(200, Applied.Replace("\"local_agent\"", $"\"{mode}\""));
        using var console = ConsoleOutput.StartFullCapture();

        await Command(server.Url).HandleAsync(["local", "intelligence", answer, "--json"]);

        await Assert.That(console.GetCapturedError().Contains(LocalIntelligenceCommand.RecordNotSavedLine)).IsEqualTo(warns);
    }

    [Test]
    public async Task A_refused_answer_keeps_the_recorded_mode() {
        new LocalIntelligenceRecord(InstallationId, "local_agent").Save(Config.Root);
        using var server = Serve(400, """{"error":"key_rejected: 401"}""");
        using var capture = ConsoleOutput.StartCapture();

        await Command(server.Url, _ => "sk").HandleAsync(["local", "intelligence", "anthropic", "--json"]);

        await Assert.That(LocalIntelligenceRecord.For(Config.Root, InstallationId).Mode).IsEqualTo("local_agent");
    }

    [Test]
    public async Task Status_reads_the_settings_into_one_json_line() {
        using var server = ServeSettings("GET", "/api/admin/intelligence", 200, Settings);
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", "status", "--json"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo(SettingsLine);
    }

    [Test]
    public async Task Status_names_each_setting_the_servers_environment_pins() {
        using var server = ServeSettings("GET", "/api/admin/intelligence", 200, Settings);
        using var stdout = ConsoleOutput.StartCapture();

        await Command(server.Url).HandleAsync(["local", "intelligence", "status"]);

        var lines = stdout.GetCapturedOutput().Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        await Assert.That(lines).IsEquivalentTo([
            "Background intelligence: openai (gpt-5-mini), with your API key.",
            "Runs: titles.",
            "Imported sessions get no outcome. The server's service environment sets this.",
            "Daily limit: 500 older sessions a day.",
        ], CollectionOrdering.Matching);
    }

    /// <summary>The record follows the mode, which a preference write never changes.</summary>
    [Test]
    public async Task Preferences_alone_put_only_what_was_given_and_touch_no_record() {
        new LocalIntelligenceRecord(InstallationId, "local_agent").Save(Config.Root);
        using var server = ServeSettings("PUT", "/api/admin/intelligence/preferences", 200, Settings);
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", "--daily-limit", "500", "--json"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(server.LogEntries.Single().RequestMessage.Body)
            .IsEqualTo("""{"import_outcomes":null,"backlog_session_touches_per_day":500}""");
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo(SettingsLine);
        await Assert.That(LocalIntelligenceRecord.For(Config.Root, InstallationId).Mode).IsEqualTo("local_agent");
    }

    [Test]
    [Arguments("GET", "/api/admin/intelligence", "status")]
    [Arguments("PUT", "/api/admin/intelligence/preferences", "--no-import-outcomes")]
    public async Task A_server_without_the_route_is_refused_as_needing_an_update(string method, string path, string arg) {
        using var server = ServeSettings(method, path, 404, "");
        using var stdout = ConsoleOutput.StartCapture();

        var exit = await Command(server.Url).HandleAsync(["local", "intelligence", arg, "--json"]);

        await Assert.That(exit).IsEqualTo(2);
        await Assert.That(stdout.GetCapturedOutput().Trim()).IsEqualTo("""{"ok":false,"error":"needs_update"}""");
    }
}
