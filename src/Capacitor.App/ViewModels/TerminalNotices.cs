using System.Windows.Input;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

/// The Terminal tab's own notice for each terminal state, in the session banner's vocabulary. A
/// read-write attach, a resolve still in flight and a session with no terminal show none.
public static class TerminalNotices {
    public static SessionConnectionPresentation? For(TerminalSessionState state, ICommand reattach, ICommand detach,
            ICommand retryResolve) => state switch {
        { Phase: TerminalSessionPhase.Connecting } => new("Connecting to the terminal…", "", true),
        { Phase: TerminalSessionPhase.Resuming } =>
            new("Resuming this session…", "The daemon restarted. The agent comes back on its own.", true),
        // The only explanation for dead keystrokes, and Detach is the only action such a session has.
        { Phase: TerminalSessionPhase.Attached, ReadOnly: true } =>
            new("Read-only terminal", state.Detail ?? "", false, "Detach", detach),
        { Phase: TerminalSessionPhase.Detached } => new("Detached from the terminal", "", false, "Reattach", reattach),
        { Phase: TerminalSessionPhase.Failed } =>
            new("The terminal disconnected", state.Detail ?? "The terminal failed.", false, "Reattach", reattach),
        { Phase: TerminalSessionPhase.NotFound } => new("Session not found", "", false, "Retry", retryResolve),
        { Phase: TerminalSessionPhase.Interrupted } => new("This session wasn’t resumed", InterruptedDetail(state.Detail), false),
        { Phase: TerminalSessionPhase.Exited } => new($"Session exited (code {state.ExitCode})", "", false, IsPlain: true),
        { Phase: TerminalSessionPhase.SessionEnded } => new("This session has ended.", "", false, IsPlain: true),
        _ => null,
    };

    public static string InterruptedDetail(string? reason) =>
        "The daemon restarted without it" + (string.IsNullOrEmpty(reason) ? "." : $" ({reason}).") +
        " Retry it with `kcap agent resume`, or stop it to discard it.";
}
