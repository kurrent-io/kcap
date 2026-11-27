using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.RepoContext;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RepoEvalMemoriesResponse))]
public partial class RepoEvalContextJsonContext : JsonSerializerContext;
