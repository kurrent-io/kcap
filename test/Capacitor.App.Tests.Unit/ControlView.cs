using Avalonia.Automation.Peers;
using Avalonia.Controls;

namespace Capacitor.App.Tests.Unit;

/// What a UI Automation client lists under a root in its default, control view: an element that is not a control
/// element is left out and its children take its place. Navigation runs through each peer's parent, as the Windows
/// provider does, so a parent that never learned of a child hides it here too.
static class ControlView {
    public static IReadOnlyList<AutomationPeer> Descendants(Control root) =>
        Descendants(ControlAutomationPeer.CreatePeerForElement(root));

    public static IReadOnlyList<AutomationPeer> Descendants(AutomationPeer root) {
        var found = new List<AutomationPeer>();
        Walk(root);
        return found;

        void Walk(AutomationPeer peer) {
            var children = peer.GetChildren();
            var child = children.Count > 0 ? children[0] : null;
            while (child is not null) {
                if (child.IsControlElement()) found.Add(child);
                Walk(child);
                child = NextSibling(child);
            }
        }
    }

    static AutomationPeer? NextSibling(AutomationPeer peer) {
        var siblings = peer.GetParent()?.GetChildren() ?? [];
        for (var i = 0; i < siblings.Count - 1; i++)
            if (ReferenceEquals(siblings[i], peer)) return siblings[i + 1];
        return null;
    }
}
