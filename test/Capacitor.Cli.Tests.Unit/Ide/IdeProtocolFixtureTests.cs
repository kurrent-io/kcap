using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Ide;
using Capacitor.Client;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>Each file under <c>test-fixtures/ide-protocol</c> is one wire message another codebase copies; the
/// .NET types must read it and write back exactly the same JSON.</summary>
public class IdeProtocolFixtureTests {
    static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-fixtures", "ide-protocol", name)))!.AsObject();

    enum Kind { Request, Notification, Result, Error }

    /// <summary>For an error, <paramref name="error"/> is the code and message it must carry, and whether it carries <c>data</c>.</summary>
    static async Task EnvelopeAsync(
            JsonObject message, Kind kind, string? method, bool withParams = true, (int Code, string Message, bool WithData)? error = null) {
        await Assert.That(message["jsonrpc"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(message.ContainsKey("id")).IsEqualTo(kind != Kind.Notification);
        await Assert.That(message.ContainsKey("method")).IsEqualTo(kind is Kind.Request or Kind.Notification);
        await Assert.That(message.ContainsKey("params")).IsEqualTo(withParams && (kind is Kind.Request or Kind.Notification));
        await Assert.That(message.ContainsKey("result")).IsEqualTo(kind == Kind.Result);
        await Assert.That(message.ContainsKey("error")).IsEqualTo(kind == Kind.Error);
        if (method is not null) await Assert.That(message["method"]!.GetValue<string>()).IsEqualTo(method);
        if (kind == Kind.Error) {
            var (code, text, withData) = error ?? throw new ArgumentException("An error fixture needs its expected error.", nameof(error));
            var body = message["error"]!.AsObject();
            string[] members = withData ? ["code", "message", "data"] : ["code", "message"];
            await Assert.That(body["code"]!.GetValue<int>()).IsEqualTo(code);
            await Assert.That(body["message"]!.GetValue<string>()).IsEqualTo(text);
            await Assert.That(body.Select(p => p.Key)).IsEquivalentTo(members);
        }
    }

    static async Task RoundTripsAsync<T>(
            string name, Kind kind, string? method, Func<JsonObject, JsonNode> select, JsonTypeInfo<T> typeInfo,
            (int Code, string Message, bool WithData)? error = null) where T : class {
        var fixture = Fixture(name);
        var node    = select(fixture);
        var value   = node.Deserialize(typeInfo);

        await Assert.That(value).IsNotNull();
        await Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value!, typeInfo), node)).IsTrue();
        await EnvelopeAsync(fixture, kind, method, error: error);
    }

    static JsonNode Params(JsonObject message) => message["params"]!;

    [Test]
    public Task Initialize_request() => RoundTripsAsync("initialize.request.json", Kind.Request, "initialize", Params, IdeJsonContext.Default.InitializeParams);

    [Test]
    public Task Initialize_result() => RoundTripsAsync("initialize.result.json", Kind.Result, null, m => m["result"]!, IdeJsonContext.Default.InitializeResult);

    [Test]
    public Task Initialize_error_unsupported() =>
        RoundTripsAsync("initialize.error.unsupported.json", Kind.Error, null, m => m["error"]!["data"]!, IdeJsonContext.Default.UnsupportedProtocolData,
            (IdeErrorCodes.UnsupportedProtocol, "unsupported_protocol", true));

    /// <summary>No data, so no type to round-trip: the envelope is the whole message.</summary>
    [Test]
    public Task Error_not_initialized() =>
        EnvelopeAsync(Fixture("error.not_initialized.json"), Kind.Error, null, error: (IdeErrorCodes.NotInitialized, "not_initialized", false));

    /// <inheritdoc cref="Error_not_initialized"/>
    [Test]
    public Task Error_method_not_found() =>
        EnvelopeAsync(Fixture("error.method_not_found.json"), Kind.Error, null, error: (IdeErrorCodes.MethodNotFound, "method_not_found", false));

    [Test]
    public Task Connection_changed() => RoundTripsAsync("connection.changed.json", Kind.Notification, "connection/changed", Params, IdeJsonContext.Default.ConnectionChanged);

    [Test]
    public Task Sessions_subscribe_request() => RoundTripsAsync("sessions.subscribe.request.json", Kind.Request, "sessions/subscribe", Params, IdeJsonContext.Default.SubscribeParams);

    [Test]
    public Task Sessions_snapshot() => RoundTripsAsync("sessions.snapshot.json", Kind.Notification, "sessions/snapshot", Params, IdeJsonContext.Default.SessionsSnapshot);

    [Test]
    public async Task Sessions_snapshot_pending_row_is_what_a_pending_launch_maps_to() {
        var fixture = Fixture("sessions.snapshot.json")["params"]!["rows"]!.AsArray()
            .Single(r => r!["origin"]!.GetValue<string>() == "pending")!;
        var launch = new PendingLaunchDto(
            "0f1e2d3c4b5a69788796a5b4c3d2e1f0", "codex", "/home/dev/src/app", "Update the docs",
            new DateTime(2026, 1, 2, 5, 14, 5, DateTimeKind.Utc), Stage: "spawning");

        var row = SessionRowMapping.From(AgentRow.FromPending(launch, new RepoIdentity("repo:acme/app", "acme/app")));

        await Assert.That(JsonSerializer.SerializeToNode(row, IdeJsonContext.Default.SessionRow)!.ToJsonString()).IsEqualTo(fixture.ToJsonString());
    }

    [Test]
    public Task Sessions_changed() => RoundTripsAsync("sessions.changed.json", Kind.Notification, "sessions/changed", Params, IdeJsonContext.Default.SessionsChanged);

    [Test]
    public Task Sessions_set_scope_request() => RoundTripsAsync("sessions.set_scope.request.json", Kind.Request, "sessions/set_scope", Params, IdeJsonContext.Default.SetScopeParams);

    [Test]
    public Task Sessions_scope_changed() => RoundTripsAsync("sessions.scope_changed.json", Kind.Notification, "sessions/scope_changed", Params, IdeJsonContext.Default.ScopeChanged);

    [Test]
    public Task Sessions_unsubscribe_request() => RoundTripsAsync("sessions.unsubscribe.request.json", Kind.Request, "sessions/unsubscribe", Params, IdeJsonContext.Default.UnsubscribeParams);

    /// <summary>No params, so no type to round-trip: the envelope is the whole message.</summary>
    [Test]
    public Task Connection_retry_request() =>
        EnvelopeAsync(Fixture("connection.retry.request.json"), Kind.Request, "connection/retry", withParams: false);
}
