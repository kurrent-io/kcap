using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.RepoInstructions;

/// <summary>One instruction file as read from the default branch: its path from the repo root, the SHA-256 of its UTF-8
/// content, and the content.</summary>
public sealed record RepoInstructionFile(
    [property: JsonPropertyName("path")]    string Path,
    [property: JsonPropertyName("sha256")]  string Sha256,
    [property: JsonPropertyName("content")] string Content);
