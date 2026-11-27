using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.RepoInstructions;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RepoInstructionsUploadRequest))]
[JsonSerializable(typeof(RepoInstructionsUploadResponse))]
[JsonSerializable(typeof(RepoInstructionsProblem))]
public partial class RepoInstructionsJsonContext : JsonSerializerContext;
