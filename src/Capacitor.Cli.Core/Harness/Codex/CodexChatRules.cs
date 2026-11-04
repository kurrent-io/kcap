using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Cli.Core.Harness.Codex;

/// Codex writes its injected preludes as user messages; the chat shows none of them as the person's,
/// nor the handoff messages agents send each other. A tool call gains the vendor-neutral kind its raw
/// name alone does not carry.
public sealed class CodexChatRules : IChatDisplayRules {
    public static readonly CodexChatRules Instance = new(ownTranscript: false);

    /// For a subagent's own transcript, where the message its parent sent is the task it was given.
    public static readonly CodexChatRules Subagent = new(ownTranscript: true);

    const string Vendor = "codex";

    readonly bool _ownTranscript;

    CodexChatRules(bool ownTranscript) => _ownTranscript = ownTranscript;

    public IReadOnlyList<string> LiteralInputs(CanonicalEvent evt, AcpEventEnvelope raw) =>
        raw is { Kind: AcpEventKind.UserMessage, Text: { Length: > 0 } text } ? [text] : [];

    /// A shell line is acknowledged as the `!` line that was typed.
    public string? SubmittedInput(CanonicalEvent evt, AcpEventEnvelope raw, AcpEventEnvelope? displayed, ScheduledPrompts? scheduled = null) =>
        displayed switch {
            { Kind: AcpEventKind.UserMessage, ToolKind: ChatDisplayKind.Shell, Text: { } shell } => "!" + shell[ShellPrefix.Length..],
            { Kind: AcpEventKind.UserMessage } user                                               => user.Text,
            _                                                                                      => null,
        };

    public AcpEventEnvelope? Filter(CanonicalEvent evt, AcpEventEnvelope envelope, ScheduledPrompts? scheduled = null) => envelope.Kind switch {
        AcpEventKind.UserMessage => UserMessage(envelope, scheduled),
        AcpEventKind.AssistantText when IsAgentMessage(evt) =>
            _ownTranscript ? envelope with { Kind = AcpEventKind.SystemNote } : null,
        AcpEventKind.ToolCall => envelope with { ToolKind = CodexToolKinds.Of(envelope.ToolName, envelope.ToolInputJson) },
        _                     => envelope,
    };

    const string ShellPrefix = "! ";

    static AcpEventEnvelope? UserMessage(AcpEventEnvelope envelope, ScheduledPrompts? scheduled) {
        var raw = envelope.Text ?? "";
        var cls = UserMessageClassifier.Classify(raw, origin: null, Vendor, scheduled);

        switch (cls.Kind) {
            case UserMessageKind.Person when cls.IsCommand && CodexShellCommand.Command(raw) is { } command:
                return envelope with { Text = ShellPrefix + command, ToolKind = ChatDisplayKind.Shell };
            case UserMessageKind.Person when cls is { IsCommand: true, Label: { Length: > 0 } label }:
                return envelope with { Text = label };
            case UserMessageKind.Person:
                // The person's words, after any context Codex wrote ahead of them in the same message.
                var text = CodexPrelude.StripLeading(raw).Trim();
                return text.Length == 0 ? null : envelope with { Text = text };
            case UserMessageKind.Trigger:
                return ChatNotes.Trigger(envelope, cls, SystemInstructionParser.Parse(raw).CleanText.Trim());
            default:
                return cls.IsInterrupt ? ChatNotes.Interrupted(envelope) : null;
        }
    }

    static bool IsAgentMessage(CanonicalEvent evt) =>
        SchemaExtensions.Slug(evt.Payload, CodexExtension.Slug)?.Fields.ContainsKey(CodexExtension.AgentMessage) == true;
}
