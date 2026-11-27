using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Eval.RepoContext;

/// <param name="Memories">The memories section a server eval of the session leads with, already rendered; empty when there is none.</param>
public sealed record RepoEvalMemoriesResponse([property: JsonPropertyName("memories")] string? Memories);
