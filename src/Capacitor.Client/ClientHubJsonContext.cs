using System.Text.Json;
using System.Text.Json.Serialization;
using Eventuous.SignalR;

namespace Capacitor.Client;

/// The hub payload types RemoteModelsJsonContext does not name, Eventuous's stream types among them: Eventuous
/// ships no generated context. No naming policy of its own, so the hub options' snake_case applies. SignalR
/// writes every argument, and binds an untyped invocation's result, as object.
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(LaunchAgentRequestV2Payload))]
[JsonSerializable(typeof(ResumeLaunchPayload))]
[JsonSerializable(typeof(NextWorkLaunchTargetPayload))]
[JsonSerializable(typeof(StreamEventEnvelope))]
[JsonSerializable(typeof(StreamSubscriptionError))]
[JsonSerializable(typeof(ulong))]
[JsonSerializable(typeof(ulong?))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(string[]))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
internal partial class ClientHubJsonContext : JsonSerializerContext;
