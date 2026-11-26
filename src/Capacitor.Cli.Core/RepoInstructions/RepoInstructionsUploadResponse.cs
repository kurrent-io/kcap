using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <param name="Status"><c>stored</c>, or <c>unchanged</c> when the repo's latest upload already carries the digest.</param>
public sealed record RepoInstructionsUploadResponse([property: JsonPropertyName("status")] string? Status);
