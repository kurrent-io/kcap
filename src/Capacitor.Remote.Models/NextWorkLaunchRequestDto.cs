using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NextWorkLaunchRequestDto(
    [property: JsonPropertyName("target_key")]           string  TargetKey,
    [property: JsonPropertyName("daemon_name")]          string  DaemonName,
    [property: JsonPropertyName("repo_path")]            string  RepoPath,
    [property: JsonPropertyName("vendor")]               string  Vendor,
    [property: JsonPropertyName("model")]                string? Model,
    [property: JsonPropertyName("daemon_owner_user_id")] string? DaemonOwnerUserId);
