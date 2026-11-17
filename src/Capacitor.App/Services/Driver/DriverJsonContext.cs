using System.Text.Json.Serialization;

namespace Capacitor.App.Services.Driver;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(DriverWorkItemsDto))]
internal partial class DriverJsonContext : JsonSerializerContext;
