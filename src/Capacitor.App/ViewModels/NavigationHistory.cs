using Capacitor.App.Services;
using Capacitor.Cli.Core;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The back and forward stacks behind the window's navigation buttons. Holds only places left
/// behind; where the window is now is read off the window, so a swap the app makes on its own
/// (a lane hand-off, an ended session) replaces the current place without an entry.
public sealed class NavigationHistory : ReactiveObject {
    internal const int Limit = 50;

    readonly List<NavigationEntry> _back = [];
    readonly List<NavigationEntry> _forward = [];

    public bool CanGoBack {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    public bool CanGoForward {
        get;
        private set => this.RaiseAndSetIfChanged(ref field, value);
    }

    /// The user moved on from `left` by their own navigation: a new branch, so forward is gone.
    public void Left(NavigationEntry left) {
        Push(_back, left);
        _forward.Clear();
        Refresh();
    }

    public NavigationEntry? TakeBack() => Take(_back);

    public NavigationEntry? TakeForward() => Take(_forward);

    /// A step back or forward arrived, leaving `left` on the opposite stack.
    public void Stepped(bool back, NavigationEntry left) {
        Push(back ? _forward : _back, left);
        Refresh();
    }

    public void Clear() {
        _back.Clear();
        _forward.Clear();
        Refresh();
    }

    NavigationEntry? Take(List<NavigationEntry> stack) {
        if (stack.Count == 0) return null;
        var top = stack[^1];
        stack.RemoveAt(stack.Count - 1);
        Refresh();
        return top;
    }

    static void Push(List<NavigationEntry> stack, NavigationEntry entry) {
        if (stack.Count > 0 && SamePlace(stack[^1], entry)) stack.RemoveAt(stack.Count - 1);
        stack.Add(entry);
        if (stack.Count > Limit) stack.RemoveAt(0);
    }

    /// An entry without a session id is still launching, so it matches its agent's later entries.
    internal static bool SamePlace(NavigationEntry a, NavigationEntry b) =>
        a.IsLauncher ? b.IsLauncher
            : !b.IsLauncher
              && AgentIds.Normalize(a.AgentId) == AgentIds.Normalize(b.AgentId)
              && (a.SessionId is null || b.SessionId is null
                  || SessionIds.SameSession(a.SessionId, b.SessionId) || SessionIds.SameSession(b.SessionId, a.SessionId));

    void Refresh() {
        CanGoBack = _back.Count > 0;
        CanGoForward = _forward.Count > 0;
    }
}
