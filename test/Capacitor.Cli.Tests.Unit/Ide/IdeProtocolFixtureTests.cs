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

    static async Task RoundTripsAsync<T>(string name, Func<JsonObject, JsonNode> select, JsonTypeInfo<T> typeInfo) where T : class {
        var fixture = Fixture(name);
        var node    = select(fixture);
        var value   = node.Deserialize(typeInfo);

        await Assert.That(value).IsNotNull();
        await Assert.That(JsonNode.DeepEquals(JsonSerializer.SerializeToNode(value!, typeInfo), node)).IsTrue();
        await Assert.That(fixture["jsonrpc"]!.GetValue<string>()).IsEqualTo("2.0");
    }

    static JsonNode Params(JsonObject message) => message["params"]!;

    [Test]
    public Task Initialize_request() => RoundTripsAsync("initialize.request.json", Params, IdeJsonContext.Default.InitializeParams);

    [Test]
    public Task Initialize_result() => RoundTripsAsync("initialize.result.json", m => m["result"]!, IdeJsonContext.Default.InitializeResult);

    [Test]
    public Task Initialize_error_unsupported() =>
        RoundTripsAsync("initialize.error.unsupported.json", m => m["error"]!["data"]!, IdeJsonContext.Default.UnsupportedProtocolData);

    [Test]
    public async Task Initialize_error_unsupported_carries_the_code_and_message() {
        var error = Fixture("initialize.error.unsupported.json")["error"]!;

        await Assert.That(error["code"]!.GetValue<int>()).IsEqualTo(IdeErrorCodes.UnsupportedProtocol);
        await Assert.That(error["message"]!.GetValue<string>()).IsEqualTo("unsupported_protocol");
    }

    [Test]
    public Task Connection_changed() => RoundTripsAsync("connection.changed.json", Params, IdeJsonContext.Default.ConnectionChanged);

    [Test]
    public Task Sessions_subscribe_request() => RoundTripsAsync("sessions.subscribe.request.json", Params, IdeJsonContext.Default.SubscribeParams);

    [Test]
    public Task Sessions_snapshot() => RoundTripsAsync("sessions.snapshot.json", Params, IdeJsonContext.Default.SessionsSnapshot);

    [Test]
    public Task Sessions_changed() => RoundTripsAsync("sessions.changed.json", Params, IdeJsonContext.Default.SessionsChanged);

    [Test]
    public Task Sessions_set_scope_request() => RoundTripsAsync("sessions.set_scope.request.json", Params, IdeJsonContext.Default.SetScopeParams);

    [Test]
    public Task Sessions_scope_changed() => RoundTripsAsync("sessions.scope_changed.json", Params, IdeJsonContext.Default.ScopeChanged);

    [Test]
    public Task Sessions_unsubscribe_request() => RoundTripsAsync("sessions.unsubscribe.request.json", Params, IdeJsonContext.Default.UnsubscribeParams);
}
