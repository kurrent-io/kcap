using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Workspace;

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(WorkspaceChangesRequest))]
[JsonSerializable(typeof(WorkspaceSpan))]
partial class WorkspaceJsonContext : JsonSerializerContext;
