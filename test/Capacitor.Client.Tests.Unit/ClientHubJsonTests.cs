using System.Text.Json;
using Capacitor.Remote.Models;
using Eventuous.SignalR;

namespace Capacitor.Client.Tests.Unit;

/// The hub payload options of a process that runs with reflection-based JSON off. Each test starts from no
/// resolver, so a type the generated contexts miss fails here rather than at runtime, where an argument
/// throws and a pushed stream event is dropped without a word.
public class ClientHubJsonTests {
    /// Every type a hub call or handler in ServerConnectionService binds, and the stream client's.
    [Test]
    public async Task Hub_payloads_serialize_without_reflection() {
        var options = new JsonSerializerOptions { TypeInfoResolver = null };
        ServerConnectionService.ConfigureHubPayload(options);
        options.MakeReadOnly();

        foreach (var type in new[] {
            typeof(List<DaemonInfo>), typeof(List<DaemonAccountsDto>), typeof(string[]),
            typeof(DiscardQueuedInputRequest), typeof(SendTerminalInputRequest), typeof(LaunchAgentRequestV2Payload),
            typeof(StreamEventEnvelope), typeof(StreamSubscriptionError), typeof(ulong), typeof(ulong?),
            typeof(JsonElement), typeof(JsonElement?), typeof(string), typeof(int), typeof(bool), typeof(object),
        })
            await Assert.That(options.TryGetTypeInfo(type, out _)).IsTrue();
    }

    /// SignalR writes each hub argument with declared type object, so its runtime type resolves through the chain.
    [Test]
    public async Task A_hub_argument_serializes_as_object_through_the_generated_contexts() {
        var options = new JsonSerializerOptions { TypeInfoResolver = null };
        ServerConnectionService.ConfigureHubPayload(options);
        var discard = new DiscardQueuedInputRequest("a1", Guid.Parse("eec4b9d3-0178-5f89-8c98-46d56ff494d4"));

        await Assert.That(JsonSerializer.Serialize<object>(discard, options))
            .IsEqualTo("""{"agent_id":"a1","dispatch_id":"eec4b9d3-0178-5f89-8c98-46d56ff494d4"}""");
        await Assert.That(JsonSerializer.Serialize<object>(34318975935UL, options)).IsEqualTo("34318975935");
    }

    [Test]
    public async Task A_pushed_stream_event_binds_from_the_snake_case_wire() {
        var options = new JsonSerializerOptions { TypeInfoResolver = null };
        ServerConnectionService.ConfigureHubPayload(options);
        const string wire = """{"event_id":"eec4b9d3-0178-5f89-8c98-46d56ff494d4","stream":"AgentSession-abc","event_type":"RepositoryDetected","stream_position":1,"global_position":34318975935,"timestamp":"2026-11-13T15:35:25.8451009Z","json_payload":"{}"}""";

        var envelope = (StreamEventEnvelope)JsonSerializer.Deserialize(wire, options.GetTypeInfo(typeof(StreamEventEnvelope)))!;

        await Assert.That(envelope.Stream).IsEqualTo("AgentSession-abc");
        await Assert.That(envelope.StreamPosition).IsEqualTo(1UL);
        await Assert.That(envelope.EventType).IsEqualTo("RepositoryDetected");
    }
}
