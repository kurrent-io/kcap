using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Capacitor.Cli.Ide;

namespace Capacitor.Cli.Tests.Unit.Ide;

/// <summary>Each file under <c>test-fixtures/ide-protocol</c> is one wire message another codebase copies; the
/// .NET types must read it and write back exactly the same JSON.</summary>
public class IdeProtocolFixtureTests {
    static JsonObject Fixture(string name) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "test-fixtures", "ide-protocol", name)))!.AsObject();

    enum Kind { Request, Notification, Result, Error }

    static async Task EnvelopeAsync(JsonObject message, Kind kind, string? method) {
        await Assert.That(message["jsonrpc"]!.GetValue<string>()).IsEqualTo("2.0");
        await Assert.That(message.ContainsKey("id")).IsEqualTo(kind != Kind.Notification);
        await Assert.That(message.ContainsKey("method")).IsEqualTo(kind is Kind.Request or Kind.Notification);
        await Assert.That(message.ContainsKey("params")).IsEqualTo(kind is Kind.Request or Kind.Notification);
        await Assert.That(message.ContainsKey("result")).IsEqualTo(kind == Kind.Result);
        await Assert.That(message.ContainsKey("error")).IsEqualTo(kind == Kind.Error);
        if (method is not null) await Assert.That(message["method"]!.GetValue<string>()).IsEqualTo(method);
        if (kind == Kind.Error) {
            await Assert.That(message["error"]!["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.UnsupportedProtocol);
            await Assert.That(message["error"]!["message"]!.GetValue<string>()).IsEqualTo("unsupported_protocol");
        }
    }

    static async Task RoundTripsAsync<T>(string name, Kind kind, string? method, Func<JsonObject, JsonNode> select, JsonTypeInfo<T> typeInfo) where T : class {
        var fixture = Fixture(name);
        var node    = select(fixture);
        var value   = node.Deserialize(typeInfo);

        await Assert.That(value).IsNotNull();
        await Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value!, typeInfo), node)).IsTrue();
        await EnvelopeAsync(fixture, kind, method);
    }

    static JsonNode Params(JsonObject message) => message["params"]!;

    [Test]
    public Task Initialize_request() => RoundTripsAsync("initialize.request.json", Kind.Request, "initialize", Params, IdeJsonContext.Default.InitializeParams);

    [Test]
    public Task Initialize_result() => RoundTripsAsync("initialize.result.json", Kind.Result, null, m => m["result"]!, IdeJsonContext.Default.InitializeResult);

    [Test]
    public Task Initialize_error_unsupported() =>
        RoundTripsAsync("initialize.error.unsupported.json", Kind.Error, null, m => m["error"]!["data"]!, IdeJsonContext.Default.UnsupportedProtocolData);

    [Test]
    public Task Connection_changed() => RoundTripsAsync("connection.changed.json", Kind.Notification, "connection/changed", Params, IdeJsonContext.Default.ConnectionChanged);

    [Test]
    public Task Sessions_subscribe_request() => RoundTripsAsync("sessions.subscribe.request.json", Kind.Request, "sessions/subscribe", Params, IdeJsonContext.Default.SubscribeParams);

    [Test]
    public Task Sessions_snapshot() => RoundTripsAsync("sessions.snapshot.json", Kind.Notification, "sessions/snapshot", Params, IdeJsonContext.Default.SessionsSnapshot);

    [Test]
    public Task Sessions_changed() => RoundTripsAsync("sessions.changed.json", Kind.Notification, "sessions/changed", Params, IdeJsonContext.Default.SessionsChanged);

    [Test]
    public Task Sessions_set_scope_request() => RoundTripsAsync("sessions.set_scope.request.json", Kind.Request, "sessions/set_scope", Params, IdeJsonContext.Default.SetScopeParams);

    [Test]
    public Task Sessions_scope_changed() => RoundTripsAsync("sessions.scope_changed.json", Kind.Notification, "sessions/scope_changed", Params, IdeJsonContext.Default.ScopeChanged);

    [Test]
    public Task Sessions_unsubscribe_request() => RoundTripsAsync("sessions.unsubscribe.request.json", Kind.Request, "sessions/unsubscribe", Params, IdeJsonContext.Default.UnsubscribeParams);
}
