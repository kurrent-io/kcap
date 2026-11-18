using Avalonia.Automation.Peers;

namespace Capacitor.App.Views;

public sealed class StatusBadgeAutomationPeer(StatusBadge owner) : ControlAutomationPeer(owner) {
    protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
    protected override bool IsContentElementCore() => true;
    protected override bool IsControlElementCore() => true;
    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;
}
