using Avalonia.Controls;

namespace Capacitor.App.Views;

/// The transcript's list: each row sits in a <see cref="ChatRowContainer"/>, which adds a reply's time
/// and Copy after the row that ends it.
public sealed class ChatItemsControl : ItemsControl {
    protected override Type StyleKeyOverride => typeof(ItemsControl);

    protected override bool NeedsContainerOverride(object? item, int index, out object? recycleKey) =>
        NeedsContainer<ChatRowContainer>(item, out recycleKey);

    protected override Control CreateContainerForItemOverride(object? item, int index, object? recycleKey) => new ChatRowContainer();

    protected override void PrepareContainerForItemOverride(Control container, object? item, int index) {
        if (container is ChatRowContainer row) row.Show(item);
        else base.PrepareContainerForItemOverride(container, item, index);
    }

    protected override void ClearContainerForItemOverride(Control container) {
        if (container is ChatRowContainer row) row.Show(null);
        else base.ClearContainerForItemOverride(container);
    }
}
