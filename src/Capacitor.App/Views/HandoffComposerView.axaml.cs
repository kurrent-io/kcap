using System.Windows.Input;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The ended session's composer. While it resumes, Enter sends as in a live session; a hand-off starts only from
/// its button, Enter there being the text box's own newline. Esc closes the hand-off settings the user opened over
/// Resume. DataContext is supplied by the host.
public partial class HandoffComposerView : UserControl {
    readonly Composer _composer;

    public HandoffComposerView() {
        InitializeComponent();
        _composer = Composer.Attach(HandoffCard, HandoffInput, HandoffAttachButton);
        HandoffInput.AddHandler(KeyDownEvent, OnInputKeyDown, RoutingStrategies.Tunnel);
    }

    void OnInputKeyDown(object? sender, KeyEventArgs e) {
        if (e.Key != Key.Enter || e.KeyModifiers.HasFlag(KeyModifiers.Shift)
         || DataContext is not HandoffComposerViewModel { ResumeVisible: true } vm) return;
        e.Handled = true;
        if (((ICommand)vm.ResumeCommand).CanExecute(null)) vm.ResumeCommand.Execute().Subscribe();
    }

    internal Task? PendingIntakeForTesting => _composer.PendingIntakeForTesting;

    protected override void OnKeyDown(KeyEventArgs e) {
        if (e.Key == Key.Escape && DataContext is HandoffComposerViewModel { CanCloseStrip: true } vm) {
            vm.CloseStrip();
            e.Handled = true;
            return;
        }
        base.OnKeyDown(e);
    }
}
