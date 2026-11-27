using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>The problem body the upload route refuses with; <c>Title</c> carries its code.</summary>
public sealed record RepoInstructionsProblem(
    [property: JsonPropertyName("title")]  string? Title,
    [property: JsonPropertyName("detail")] string? Detail);
