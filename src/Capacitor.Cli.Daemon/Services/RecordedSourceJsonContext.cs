using System.Text.Json.Serialization;

namespace Capacitor.Cli.Daemon.Services;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(List<RecordedSource>))]
internal partial class RecordedSourceJsonContext : JsonSerializerContext;
