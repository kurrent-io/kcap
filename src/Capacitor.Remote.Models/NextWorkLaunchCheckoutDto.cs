using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

public sealed record NextWorkLaunchCheckoutDto(
    [property: JsonPropertyName("daemon_name")]          string        DaemonName,
    [property: JsonPropertyName("repo_path")]            string        RepoPath,
    [property: JsonPropertyName("vendors")]              List<string>? Vendors,
    [property: JsonPropertyName("daemon_owner_user_id")] string?       DaemonOwnerUserId);
