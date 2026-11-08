namespace Capacitor.App.ViewModels;

/// What the app says of an attached agent: a Claude Code session in its owner's own terminal, which kcap steers
/// through the owner's daemon rather than hosts. The web and mobile apps show most of these word for word, and a
/// change to one of those has to be made there too.
public static class AttachedAgentCopy {
    public const string TerminalSession = "Terminal session";
    public const string TerminalSessionTip = "A Claude Code session in its owner's terminal, steered through their daemon";
    public const string DisconnectSteering = "Disconnect steering";
    public const string DisconnectFailed = "Failed to disconnect steering";
    public const string WaitingForApproval = "Waiting for approval at the terminal";
    public const string WaitingForAnswer = "Waiting for an answer at the terminal";
    /// An attached agent that is gone: the steering ended, while its Claude Code session may still run in its terminal.
    public const string SteeringEnded = "Steering ended";
    public const string InterruptAtTerminal = "Interrupt it in its terminal";
    public const string NoFiles = "files can't be sent to a terminal session";

    public const string WaitingForTheTurn = "Waiting for the turn to end";
    public const string Delivered = "Delivered";
    public const string SendAgain = "Not delivered, send again";
    public const string DeliveryUnknown = "Delivery unknown, check the session before sending again";

    /// The state as the app reads it: null for a hosted agent's message and for a state this build does not know, so
    /// that either shows as a hosted message does.
    public static string? Known(string? state) =>
        state is "held" or "with_mod" or "delivered" or "rejected" or "unconfirmed" ? state : null;

    /// Only a message that cannot land tells the sender to send again, and only where sending again could land.
    public static string? Delivery(string? state, string? reason) => Known(state) switch {
        "held" or "with_mod" => WaitingForTheTurn,
        "delivered"          => Delivered,
        "unconfirmed"        => DeliveryUnknown,
        "rejected"           => reason switch {
            "stopped"                 => "Not delivered, the session was stopped at the terminal",
            "command"                 => "Not delivered, commands can only be run at the terminal",
            "unconfirmed"             => "Not delivered, an earlier message's delivery is still unknown",
            "attachments_unsupported" => "Not delivered, files can't be sent to a terminal session",
            _                         => SendAgain,
        },
        _ => null,
    };

    /// Delivered or not, as far as the server can tell: no longer waiting for the agent.
    public static bool IsSettled(string? state) => Known(state) is "delivered" or "rejected" or "unconfirmed";

    /// A settled state the sender has to act on.
    public static bool NeedsSender(string? state) => Known(state) is "rejected" or "unconfirmed";
}
