using System.Text.Json.Serialization;

namespace Capacitor.Cli.Daemon.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(InputLogState))]
internal partial class InputLogJsonContext : JsonSerializerContext;
