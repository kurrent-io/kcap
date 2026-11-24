namespace Capacitor.App.ViewModels;

/// Glyph, word, and hover text for one resolved session status. The accessible name is the
/// tooltip's first line, so a screen reader and a hover agree.
public sealed record AgentStatusPresentation(
        AgentStatusKind Kind, string Label, string Tip, bool Pulses, IReadOnlyList<AgentStatusFact> Facts) {
    public static AgentStatusPresentation None { get; } =
        new(AgentStatusKind.Other, "", "", false, []);

    /// The session is over, so a daemon word it last reported no longer means it is live.
    public bool Ended { get; init; }

    public string AccessibleName => Tip.Length == 0 ? "" : Tip.Split('\n')[0];

    public bool HasLabel => Label.Length > 0;
    /// Working, and the daemon has proof a turn is running rather than an idle prompt that never
    /// reported a turn end. Only this makes a mid-turn keystroke like Escape safe to send.
    public bool InForegroundTurn { get; init; }

    /// The daemon says Running but the turn is unknown (a remote row): live, yet not claimed as Working.
    public bool IsRunningUnknownTurn => !Ended && Kind == AgentStatusKind.Other && Label == "Running";
    public bool IsInFlight => Kind is AgentStatusKind.Working or AgentStatusKind.Starting or AgentStatusKind.Resuming || IsRunningUnknownTurn;
    public bool IsWarning => Kind is AgentStatusKind.Idle or AgentStatusKind.Stale or AgentStatusKind.NeedsYou or AgentStatusKind.Answer
        or AgentStatusKind.Interrupted or AgentStatusKind.DaemonDown;
    public bool IsDanger => Kind == AgentStatusKind.Failed;
    public bool ShowsRing => IsInFlight;
    public bool ShowsClock => Kind is AgentStatusKind.Idle or AgentStatusKind.Stale;
    public bool ShowsAsk => Kind == AgentStatusKind.Answer;
    public bool ShowsBang => Kind == AgentStatusKind.NeedsYou;
    public bool ShowsPause => Kind is AgentStatusKind.Interrupted or AgentStatusKind.DaemonDown;
    public bool ShowsCross => Kind == AgentStatusKind.Failed;
    public bool IsDone => Kind == AgentStatusKind.Done;
    public bool ShowsSettledMark => Kind is AgentStatusKind.Done or AgentStatusKind.Other && !IsRunningUnknownTurn;

    public bool WaitsForInput { get; init; }
    public bool IsDirectRequest => Kind is AgentStatusKind.Answer or AgentStatusKind.NeedsYou
        || ShowsInputRequest;

    bool ShowsInputRequest => WaitsForInput && !Ended
        && Kind is AgentStatusKind.Idle or AgentStatusKind.Working or AgentStatusKind.Stale or AgentStatusKind.Other;

    public bool IsActionable => IsDirectRequest || IsWarning || IsDanger;

    internal string? RollupLabel { get; init; }
    public string PreviewLabel => RollupLabel ?? (ShowsInputRequest ? "Waiting for input"
        : Ended && Kind is AgentStatusKind.Done or AgentStatusKind.Other ? "Ended" : Label);

    public string PreviewAccessibleName => PreviewLabel == AccessibleName || PreviewLabel != Label
        ? PreviewLabel : $"{PreviewLabel}. {AccessibleName}";

    public StatusGlyphKind PreviewGlyph => Kind switch {
        AgentStatusKind.Failed => StatusGlyphKind.Failure,
        AgentStatusKind.Answer => StatusGlyphKind.Input,
        AgentStatusKind.NeedsYou => StatusGlyphKind.Attention,
        AgentStatusKind.Interrupted or AgentStatusKind.DaemonDown => StatusGlyphKind.Interrupted,
        _ when ShowsInputRequest => StatusGlyphKind.Input,
        AgentStatusKind.Idle or AgentStatusKind.Stale => StatusGlyphKind.Stale,
        AgentStatusKind.Done => Ended ? StatusGlyphKind.Stopped : StatusGlyphKind.Success,
        _ when IsInFlight && !Ended => StatusGlyphKind.Activity,
        _ when Ended || Label is "Stopped" or "Cancelled" or "Ended" => StatusGlyphKind.Stopped,
        _ => StatusGlyphKind.Unknown,
    };
}
