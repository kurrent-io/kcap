using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.Http;

public record UserCandidatesDto([property: JsonPropertyName("candidates")] List<UserCandidateDto>? Candidates);
