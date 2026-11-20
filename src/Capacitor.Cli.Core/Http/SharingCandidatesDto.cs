using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record SharingCandidatesDto([property: JsonPropertyName("candidates")] List<SharingCandidateDto> Candidates);
