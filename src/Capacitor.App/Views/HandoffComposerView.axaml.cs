using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The ended session's composer. Enter is the text box's own newline: a resume or a hand-off starts
/// only from its button, never from a key typed into a prompt. Esc closes the hand-off settings the
/// user opened over Resume. DataContext is supplied by the host.
public partial class HandoffComposerView : UserControl {
    readonly Composer _composer;

    public HandoffComposerView() {
        InitializeComponent();
        _composer = Composer.Attach(HandoffCard, HandoffInput, HandoffAttachButton);
    }

    internal Task? PendingIntakeForTesting => _composer.PendingIntakeForTesting;

    internal void FocusComposer() => HandoffInput.Focus();

    void OnComposerOverflowChanged(object? sender, ScrollChangedEventArgs e) {
        if (e.Source == sender && sender is ScrollViewer scroll)
            scroll.Classes.Set("hasOverflow", scroll.Extent.Height > scroll.Viewport.Height + 1);
    }

    void OnLaunchClick(object? sender, RoutedEventArgs e) {
        if (ComposerPresentation.GetIsPreview(this)) FocusComposer();
    }

    protected override void OnKeyDown(KeyEventArgs e) {
        if (!e.Handled && !_composer.IsComposing && e.Key == Key.Escape && DataContext is HandoffComposerViewModel { CanCloseStrip: true } vm) {
            vm.CloseStrip();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
