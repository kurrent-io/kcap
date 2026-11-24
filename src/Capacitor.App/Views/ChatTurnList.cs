using Avalonia.Automation.Peers;
using Avalonia.Automation.Provider;
using Avalonia.Controls;

namespace Capacitor.App.Views;

internal sealed class ChatTurnList : ListBox {
    internal Action<ChatTurnOutlineEntry>? Activate { get; set; }

    protected override Type StyleKeyOverride => typeof(ListBox);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) =>
        new TurnRow { IsTabStop = false };

    sealed class TurnRow : ListBoxItem {
        protected override Type StyleKeyOverride => typeof(ListBoxItem);
        protected override AutomationPeer OnCreateAutomationPeer() => new TurnRowPeer(this);
    }

    sealed class TurnRowPeer(TurnRow row) : ListItemAutomationPeer(row), IInvokeProvider {
        protected override string GetNameCore() =>
            row.Content is ChatTurnOutlineEntry entry ? entry.AccessibleName : base.GetNameCore() ?? "";

        public void Invoke() {
            EnsureEnabled();
            if (row.IsEffectivelyVisible && row.Parent is ChatTurnList list && list.IndexFromContainer(row) >= 0
                && row.Content is ChatTurnOutlineEntry entry) list.Activate?.Invoke(entry);
        }
    }
}
