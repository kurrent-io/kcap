using System.Text.RegularExpressions;

namespace Capacitor.App.Services.Voice;

/// How much a new session may do without asking, in the words people say. Each harness maps it
/// onto its own setting: Claude's permission mode, Codex's sandbox.
public enum VoicePermissionLevel {
    Manual,
    AcceptEdits,
    Auto,
    Bypass,
}

public static partial class VoicePermissionLevels {
    /// The level a phrase names ("bypass permissions", "full access", "accept edits"), or null.
    public static VoicePermissionLevel? Parse(string phrase) => Normalize(phrase) switch {
        "manual" or "manual permissions" or "ask" or "ask me" or "ask for everything" or "ask first" or "normal permissions"
            or "default permissions" or "read only" => VoicePermissionLevel.Manual,
        "accept edits" or "auto accept edits" or "accept edits mode" or "edit mode" or "workspace write" => VoicePermissionLevel.AcceptEdits,
        "auto" or "auto mode" or "automatic" or "auto permissions" => VoicePermissionLevel.Auto,
        "bypass" or "bypass permissions" or "bypass mode" or "bypass all permissions" or "yolo" or "yolo mode" or "full access"
            or "no permissions" or "no permission prompts" or "without permissions" or "dangerously skip permissions"
            or "skip permissions" => VoicePermissionLevel.Bypass,
        _ => null,
    };

    /// The phrases <see cref="Parse"/> reads, for building the parser's patterns.
    public const string Pattern =
        "manual( permissions)?|ask( me)?( for everything| first)?|normal permissions|default permissions|read only|" +
        "(auto )?accept edits( mode)?|edit mode|workspace write|auto( mode| permissions)?|automatic|" +
        "bypass( all)?( permissions| mode)?|yolo( mode)?|full access|no permissions|no permission prompts|without permissions|" +
        "dangerously skip permissions|skip permissions";

    static string Normalize(string phrase) => Spaces().Replace(phrase.Trim().ToLowerInvariant().Replace("-", " ", StringComparison.Ordinal), " ")
        .Replace(" mode mode", " mode", StringComparison.Ordinal);

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();
}
