using Capacitor.Models.Transcripts.Harness.Claude;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.UserMessages;

/// What a harness recorded about who wrote a user message, beside its text. The classifier decides
/// on these before it reads the text.
public sealed record UserMessageOrigin {
    /// Claude Code's `isMeta`: harness-injected user-role content such as a skill body.
    public bool IsMeta { get; init; }

    /// Claude Code's `origin.kind`: `human`, `task-notification`, `peer`, `coordinator`, `auto-continuation`.
    public string? Kind { get; init; }

    /// Claude Code's `origin.handback`: a peer message that is this session's own subagent reporting back.
    public bool IsHandback { get; init; }

    /// Claude Code's `isCompactSummary`.
    public bool IsCompactSummary { get; init; }

    /// The command line of a Claude Code `local_command` record, which is that command's receipt.
    public string? LocalCommand { get; init; }

    /// Codex's content item kinds (0.155+). A part whose kind is not `user.*` is Codex's own context.
    public IReadOnlyList<string>? ContentItemKinds { get; init; }

    /// The Claude Code fields stored on the message's `claude_code` extension, or null when it carries none.
    public static UserMessageOrigin? Of(UserMessageReceived message) {
        var slug = SchemaExtensions.Slug(message, ClaudeCodeExtension.Slug);
        if (slug is null) return null;

        return new UserMessageOrigin {
            IsMeta           = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsMeta),
            Kind             = SchemaExtensions.Text(slug, ClaudeCodeExtension.OriginKind),
            IsHandback       = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsHandback),
            IsCompactSummary = SchemaExtensions.Flag(slug, ClaudeCodeExtension.IsCompactSummary),
            LocalCommand     = SchemaExtensions.Text(slug, ClaudeCodeExtension.LocalCommand),
        };
    }
}
