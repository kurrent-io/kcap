using Avalonia.Automation.Peers;

namespace Capacitor.App.Views;

internal sealed class WarmupHostAutomationPeer(WarmupHost owner) : ControlAutomationPeer(owner) {
    protected override IReadOnlyList<AutomationPeer>? GetChildrenCore() => null;
    protected override bool IsControlElementCore() => false;
    protected override bool IsContentElementCore() => false;
}
