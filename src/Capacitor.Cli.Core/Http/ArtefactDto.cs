using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>One artefact as the server describes it. Mirrors the server's wire shape rather than
/// referencing it: this binary ships independently and has to keep parsing what an older or newer
/// server sends.</summary>
/// <param name="Visibility">The audience on a server without <paramref name="SharedWith"/>; fixed text on one with it.</param>
/// <param name="ObjectKey">The key <c>/api/sharing</c> takes. Null from a server kcap cannot share through.</param>
/// <param name="SharedWith">The grants the caller is shown. Null from a server that does not send them.</param>
public record ArtefactDto(
    [property: JsonPropertyName("artefact_id")]    string                 ArtefactId,
    [property: JsonPropertyName("title")]          string                 Title,
    [property: JsonPropertyName("owner_user_id")]  string                 OwnerUserId,
    [property: JsonPropertyName("visibility")]     string                 Visibility,
    [property: JsonPropertyName("latest_version")] int                    LatestVersion,
    [property: JsonPropertyName("updated_at")]     DateTimeOffset         UpdatedAt,
    [property: JsonPropertyName("is_owner")]       bool                   IsOwner,
    [property: JsonPropertyName("object_key")]     string?                ObjectKey,
    [property: JsonPropertyName("shared_with")]    List<SharedGrantDto>?  SharedWith,
    [property: JsonPropertyName("url")]            string                 Url);
