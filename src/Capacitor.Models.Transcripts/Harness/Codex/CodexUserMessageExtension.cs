using System.Text.Json.Serialization;

namespace Capacitor.Models.Transcripts.Harness.Codex;

/// `extensions.codex` on a user message Codex 0.155+ annotated: the turn it opens and the kind of
/// each content part, where a part whose kind is not a `user.` one is Codex's own context. Older
/// rollouts carry neither, and a stored event the server wrote keeps them only in its metadata.
public sealed record CodexUserMessageExtension {
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? TurnId { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? ContentItemKinds { get; init; }
}
