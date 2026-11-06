using System.Windows.Input;
using Avalonia.Controls;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A Ctrl shortcut on Linux and Windows that is also a terminal key. CanExecute is false while
/// focus is inside the terminal, so the key stays unhandled and the terminal sends it.
sealed class UnlessTerminalFocused(TopLevel window, Func<ICommand?> resolve) : ICommand {
    /// For a command the window's view model owns; there is none until the window has one.
    public UnlessTerminalFocused(TopLevel window, Func<MainWindowViewModel, ICommand> pick)
        : this(window, () => window.DataContext is MainWindowViewModel vm ? pick(vm) : null) { }

    public UnlessTerminalFocused(TopLevel window, ICommand command) : this(window, () => command) { }

    public bool CanExecute(object? parameter) =>
        !FocusedInTerminal() && Inner?.CanExecute(parameter) == true;

    public void Execute(object? parameter) {
        if (FocusedInTerminal() || Inner is not { } inner || !inner.CanExecute(parameter)) return;
        inner.Execute(parameter);
    }

    public event EventHandler? CanExecuteChanged { add { } remove { } }

    ICommand? Inner => resolve();

    bool FocusedInTerminal() => TerminalFocus.Has(window);
}
