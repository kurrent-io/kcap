using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.LocalIpc;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ResumeProbeRequestDto))]
[JsonSerializable(typeof(ResumeProbeReplyDto))]
public partial class ResumeInPlaceIpcJsonContext : JsonSerializerContext;
