using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(SteeringSetAckDto))]
public partial class SteeringIpcJsonContext : JsonSerializerContext;
