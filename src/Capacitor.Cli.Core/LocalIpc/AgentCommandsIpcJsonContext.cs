using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HostedAgentCommand[]))]
public partial class AgentCommandsIpcJsonContext : JsonSerializerContext;
