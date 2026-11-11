using System.Text.Json.Serialization;

namespace Capacitor.Cli.Daemon.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(SteeringAttachRequest))]
[JsonSerializable(typeof(SteeringNextRequest))]
[JsonSerializable(typeof(SteeringLinkRequest))]
[JsonSerializable(typeof(SteeringUndeliveredRequest))]
[JsonSerializable(typeof(SteeringDetachRequest))]
[JsonSerializable(typeof(SteeringAttachReply))]
[JsonSerializable(typeof(SteeringNextReply))]
internal sealed partial class SteeringJsonContext : JsonSerializerContext;
