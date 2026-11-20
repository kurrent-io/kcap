using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

public enum TrayState { Stopped, Connecting, Attention, Idle, Running }

// StopEnabled: false while AgentActionService.StopsInFlight contains Key. Kind is the wire
// KindText spelling (agent|review|review-flow|attached) — carried through so the Stop click handler can
// pass it to AgentActionService.RequestStop, which decides protected-ness. Origin (default
// Local) is threaded through to the same RequestStop call so a remote entry's stop reaches the
// server lane rather than the local socket.
public sealed record TrayAgentEntry(string Id, string Label, string Kind, bool StopEnabled, AgentOrigin Origin = AgentOrigin.Local) {
    public string StopLabel => Kind == AgentActionService.AttachedKind ? AttachedAgentCopy.DisconnectSteering : "Stop";

    /// What the menu items carry as their command parameter, and how this entry's stop is named in
    /// AgentActionService.StopsInFlight: a local and a remote entry can share an agent id, and only
    /// this tells the two apart.
    public string Key => AgentActionService.StopKey(Origin, Id);
}
public sealed record TrayPauseItem(bool Enabled, bool Checked);

/// The server lane's contribution to the tray verdict: live remote agents (twin-suppressed rows and
/// attached agents excluded already), whether the lane is up, and any remote sessions the owner has
/// been asked to attend to (a permission/question on that session) — SessionsNeedingAttention counts
/// AttentionEntries, never derived independently. An attached agent's entry can still name a session
/// this machine's daemon steers; the tray drops that one against its local entry.
public readonly record struct RemoteTraySummary(
    int RemoteLiveAgents, bool LaneConnected, int SessionsNeedingAttention = 0,
    IReadOnlyList<TrayAgentEntry>? AttentionEntries = null);
// UpdateItemLabel: the coordinator's current label for the tray's single update item, or null
// while it should not show at all.
public sealed record TrayMenuModel(
    TrayState State, int RunningCount, string Header,
    IReadOnlyList<TrayAgentEntry> Agents, TrayPauseItem Pause, int PendingConsent, bool ShimInstallVisible = false,
    string? UpdateItemLabel = null, bool FinishSwitchVisible = false, string? ToolTipHint = null) {
    const int ToolTipLimit = 127;

    /// The tray icon's tooltip, which screen readers also read as its name. Windows keeps only
    /// the first 127 characters of a notification-area tooltip, so a hint goes first.
    public string ToolTip {
        get {
            var text = ToolTipHint is { } hint ? $"{hint}\nkcap — {Header}" : $"kcap — {Header}";
            return text.Length <= ToolTipLimit ? text : text[..(ToolTipLimit - 1)] + "…";
        }
    }
}
