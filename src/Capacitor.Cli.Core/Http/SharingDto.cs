using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

/// <summary>An object's grants as the caller is shown them. <see cref="ManageRefusal"/> says why a caller who could
/// manage was refused.</summary>
public record SharingDto(
    [property: JsonPropertyName("can_manage")]     bool                  CanManage,
    [property: JsonPropertyName("manage_refusal")] string?               ManageRefusal,
    [property: JsonPropertyName("grants")]         List<SharingGrantDto> Grants);
