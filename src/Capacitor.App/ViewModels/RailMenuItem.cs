using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// A RailAction bound to the rows one menu targets.
public sealed record RailMenuItem(LucideIconKind Icon, string Label, Action Run);
