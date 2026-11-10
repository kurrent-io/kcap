using System.Text.Json.Serialization;

namespace Capacitor.App.Services.CliAdoption;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AdoptionRecord))]
internal partial class AdoptionRecordJsonContext : JsonSerializerContext;
