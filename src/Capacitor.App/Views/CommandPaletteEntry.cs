using System.Windows.Input;
using Avalonia.Controls;

namespace Capacitor.App.Views;

internal sealed record CommandPaletteEntry(string Scope, string? Label, ICommand Command, string? Shortcut = null, Button? Source = null) {
    public string Title => Label ?? Source?.Content as string ?? "";
    public object? Parameter => Source?.CommandParameter;

    public bool IsAvailable => (Source is null || (Source.IsEffectivelyVisible && Source.IsEffectivelyEnabled
        && ReferenceEquals(Source.Command, Command))) && Command.CanExecute(Parameter);
}
