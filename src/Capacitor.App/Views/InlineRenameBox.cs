using System.ComponentModel;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A title renamed where it stands: the field opens with the title selected, Enter or leaving it saves, Escape cancels.
public sealed class InlineRenameBox : TextBox {
    SessionTitleEditorViewModel? _model;
    TopLevel? _window;

    protected override Type StyleKeyOverride => typeof(TextBox);

    public InlineRenameBox() {
        AddHandler(KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        LostFocus += (_, _) => Run(_model?.SaveCommand);
        DataContextChanged += (_, _) => {
            if (_model is not null) _model.PropertyChanged -= OnModelChanged;
            _model = DataContext as SessionTitleEditorViewModel;
            if (_model is not null) _model.PropertyChanged += OnModelChanged;
            WatchPressesElsewhere(_model is not null);
        };
        DetachedFromVisualTree += (_, _) => WatchPressesElsewhere(false);
    }

    // A press on blank space moves no focus, so leaving the field is watched as a press anywhere else in the window.
    void WatchPressesElsewhere(bool watch) {
        _window?.RemoveHandler(PointerPressedEvent, OnPressElsewhere);
        _window = watch ? TopLevel.GetTopLevel(this) : null;
        _window?.AddHandler(PointerPressedEvent, OnPressElsewhere, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    void OnPressElsewhere(object? sender, PointerPressedEventArgs e) {
        if (e.Source is Visual pressed && (pressed == this || this.IsVisualAncestorOf(pressed))) return;
        Run(_model?.SaveCommand);
    }

    // A failed save hands the field back for another try.
    void OnModelChanged(object? sender, PropertyChangedEventArgs e) {
        var entering = e.PropertyName == nameof(SessionTitleEditorViewModel.IsEditing);
        if (!entering && e.PropertyName != nameof(SessionTitleEditorViewModel.IsSaving)) return;
        if (sender is not SessionTitleEditorViewModel { IsEditing: true, IsSaving: false }) return;
        Dispatcher.UIThread.Post(() => {
            if (!ReferenceEquals(sender, _model)) return;
            Focus();
            if (entering) SelectAll();
        }, DispatcherPriority.Loaded);
    }

    void OnKeyDown(object? sender, KeyEventArgs e) {
        ICommand? command = e.Key switch {
            Key.Enter => _model?.SaveCommand,
            Key.Escape => _model?.CancelCommand,
            _ => null,
        };
        if (command is null) return;
        e.Handled = true;
        Run(command);
    }

    static void Run(ICommand? command) {
        if (command?.CanExecute(null) == true) command.Execute(null);
    }
}
