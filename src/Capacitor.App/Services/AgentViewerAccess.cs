using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// What the viewer may do to a hosted agent on another machine, read from the rights the server sends. A server with
/// agent privileges off sends none, and the viewer is then unrestricted. A viewer who sees the agent without reaching
/// it may only watch.
public sealed record AgentViewerAccess(
        bool Reaches, bool IsOwner, bool AnswerQuestions, bool ApproveToolUse, bool HoldMessages, bool SendsDirect,
        bool HasRights) {
    public static readonly AgentViewerAccess Unrestricted = new(true, false, true, true, false, true, false);

    public const string HeldReason = "Only the owner can control the terminal while messages need approval";
    public const string ViewOnlyReason = "You can watch this agent. Sending messages or answering needs Full access from its owner";
    public const string QuestionPendingNote = "The agent is waiting on a question you can't answer";

    public static AgentViewerAccess From(AgentViewerRightsDto? rights) {
        if (rights is null) return Unrestricted;
        var reaches = rights.IsOwner || rights.Reaches;
        return new(
            reaches,
            rights.IsOwner,
            reaches && rights.AnswerQuestions,
            reaches && rights.ApproveToolUse,
            rights.HoldMessages,
            rights.IsOwner || rights.SendWithoutApproval,
            HasRights: true);
    }

    /// A terminal session the daemon steers: only its terminal can answer its prompts, whoever the viewer is.
    public bool AtTerminal { get; init; }

    /// The viewer's messages wait for the owner, and they may neither press keys, stop nor resize the agent.
    public bool InputHeld => Reaches && HoldMessages && !SendsDirect;

    public bool CanSend => Reaches;

    public bool CanPressKeys => Reaches && !InputHeld;

    /// Why the viewer may not press keys or stop the agent; for a viewer who only watches, why they may not send either.
    public string? ControlRefusal => !Reaches ? ViewOnlyReason : InputHeld ? HeldReason : null;

    public string? ApprovalMode => this switch {
        { Reaches: false } or { HoldMessages: false } => null,
        { IsOwner: true } => "Messages from others wait for your approval",
        { SendsDirect: true } => "Message approval is on, and your messages go straight to the agent",
        _ => "Your messages wait for the owner's approval",
    };

    /// A question needs Answer questions; a permission needs Approve tool use.
    public bool CanAnswer(PendingPermissionRequest request) => !AtTerminal && (request.IsQuestion ? AnswerQuestions : ApproveToolUse);

    /// Who a prompt the viewer cannot answer waits on; null when they can answer it.
    public string? WaitingOn(PendingPermissionRequest request) =>
        AtTerminal ? ViewModels.AttachedAgentCopy.WaitingForApproval
        : CanAnswer(request) ? null
        : request.IsQuestion ? "Waiting for the owner to answer"
        : "Waiting for the owner to decide";
}
