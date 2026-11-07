using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(RecordSourceDto))]
[JsonSerializable(typeof(RecordTerminalSourceDto))]
[JsonSerializable(typeof(DisableSessionAckDto))]
public partial class RecordingIpcJsonContext : JsonSerializerContext;
