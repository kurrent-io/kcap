namespace Capacitor.Cli.Daemon.Services;

/// <summary>How <see cref="AgentOrchestrator.DeliverInputAsync"/> ended. <c>QuitRequested</c> is not
/// a failure: the text was a quit command a TUI-less runtime cannot interpret, and stopping the
/// agent belongs to the caller, which owns the lane the stop must ride.</summary>
internal enum InputDeliveryKind { Delivered, QuitRequested, Dropped }

/// <summary>One delivery attempt's result. <see cref="Reason"/> carries an
/// <see cref="AgentOrchestrator.SendInputDropReason"/> token when the input was dropped. An attachment
/// refusal's <see cref="Error"/> is what the report sends instead, so the sender can read which refusal
/// it was; every other error stays in the log. A delivered dispatch's <see cref="Written"/> completes
/// once the runtime has written it, which can be long after delivery returns.</summary>
internal readonly record struct InputDeliveryOutcome(
        InputDeliveryKind Kind, string? Reason = null, string? Error = null, Task? Written = null) {
    public static readonly InputDeliveryOutcome Delivered     = new(InputDeliveryKind.Delivered);
    public static readonly InputDeliveryOutcome QuitRequested = new(InputDeliveryKind.QuitRequested);

    public static InputDeliveryOutcome Drop(string reason, string? error = null) =>
        new(InputDeliveryKind.Dropped, reason, error);
}
