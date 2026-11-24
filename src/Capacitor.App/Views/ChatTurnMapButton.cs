using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Capacitor.App.Views;

internal sealed class ChatTurnMapButton : Button {
    internal Action<bool>? ChangeOutline { get; set; }
    ExpandCollapseState _outlineState = ExpandCollapseState.Collapsed;

    protected override Type StyleKeyOverride => typeof(Button);
    protected override AutomationPeer OnCreateAutomationPeer() => new MapButtonPeer(this);

    internal void SetOutlineOpen(bool open) {
        var next = open ? ExpandCollapseState.Expanded : ExpandCollapseState.Collapsed;
        var previous = _outlineState;
        if (next == previous) return;
        _outlineState = next;
        ControlAutomationPeer.FromElement(this)?.RaisePropertyChangedEvent(
            ExpandCollapsePatternIdentifiers.ExpandCollapseStateProperty, previous, next);
    }

    sealed class MapButtonPeer(ChatTurnMapButton button) : ButtonAutomationPeer(button), IExpandCollapseProvider {
        public ExpandCollapseState ExpandCollapseState => button._outlineState;
        public bool ShowsMenu => false;

        public void Expand() {
            EnsureEnabled();
            button.ChangeOutline?.Invoke(true);
        }

        public void Collapse() {
            EnsureEnabled();
            button.ChangeOutline?.Invoke(false);
        }
    }
}
