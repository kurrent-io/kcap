using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RecordSourceDto))]
public partial class RecordingIpcJsonContext : JsonSerializerContext;
