using System.Text.Json.Serialization;

namespace Capacitor.Cli.Daemon.Harness.Claude;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(ModAttachmentFile))]
internal sealed partial class ModAttachmentJsonContext : JsonSerializerContext;
