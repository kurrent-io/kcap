using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// One entry of a row's right-click menu, bound to the rows it acts on. <see cref="IconBrushKey"/> names an app
/// brush resource for the icon; null keeps the text colour.
public sealed record RowMenuItem(LucideIconKind Icon, string Label, Action Run, string? IconBrushKey = null);
