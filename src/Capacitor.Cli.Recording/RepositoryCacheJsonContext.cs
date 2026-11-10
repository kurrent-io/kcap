using System.Text.Json.Serialization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording;

// CapacitorJsonContext's options: a file it wrote has to read back through this one.
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, UseStringEnumConverter = true)]
[JsonSerializable(typeof(GitCacheEntry))]
[JsonSerializable(typeof(RepositoryPayload))]
internal partial class RepositoryCacheJsonContext : JsonSerializerContext;
