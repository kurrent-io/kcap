using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// One entry of a row's right-click menu, bound to the rows it acts on.
public sealed record RowMenuItem(LucideIconKind Icon, string Label, Action Run);
