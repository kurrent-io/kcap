using Avalonia;
using Avalonia.Controls;

namespace Capacitor.App.Views;

/// Full-width row host that sizes its card child to the row, left-aligned — Stretch on a card whose
/// style caps its width would center the slack in Avalonia.
public partial class ChatCardRow : ContentControl {
    protected override void OnSizeChanged(SizeChangedEventArgs e) {
        base.OnSizeChanged(e);
        ApplyChildWidth();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);
        if (change.Property == ContentProperty) ApplyChildWidth();
    }

    void ApplyChildWidth() {
        if (Content is not Control child || Bounds.Width <= 0) return;
        child.Width = Bounds.Width;
        child.HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
    }
}
