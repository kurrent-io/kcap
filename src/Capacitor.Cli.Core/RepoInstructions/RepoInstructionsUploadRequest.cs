using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>Body of <c>POST /api/repos/instructions</c>; an empty <c>Files</c> records that the repo holds none.</summary>
public sealed record RepoInstructionsUploadRequest(
    [property: JsonPropertyName("session_id")] string                             SessionId,
    [property: JsonPropertyName("digest")]     string                             Digest,
    [property: JsonPropertyName("files")]      IReadOnlyList<RepoInstructionFile> Files);
