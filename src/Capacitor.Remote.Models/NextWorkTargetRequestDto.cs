using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// <summary>The body of the next-work dismiss and restore calls.</summary>
public sealed record NextWorkTargetRequestDto(
    [property: JsonPropertyName("target_key")] string  TargetKey,
    [property: JsonPropertyName("repo_hash")]  string? RepoHash);
