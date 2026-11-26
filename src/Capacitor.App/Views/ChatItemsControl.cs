using Avalonia.Controls;
using Avalonia.Controls.Templates;

namespace Capacitor.App.Views;

/// The transcript's list: each row sits in a <see cref="ChatRowContainer"/>, which adds a reply's time
/// and Copy after the row that ends it. Containers recycle by template, so a recycled one keeps its row.
public sealed class ChatItemsControl : ItemsControl {
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) {
        if (item is Control) {
            recycleKey = null;
            return false;
        }
        recycleKey = this.FindDataTemplate(item, ItemTemplate) ?? (object)typeof(ChatRowContainer);
        return true;
    }

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ChatRowContainer();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index) {
        if (container is ChatRowContainer row) row.Show(item, this.FindDataTemplate(item, ItemTemplate));
        else base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ClearContainerForItemOverride(Control container) {
        if (container is ChatRowContainer row) row.Park();
        else base.ClearContainerForItemOverride(container);
    }
}
