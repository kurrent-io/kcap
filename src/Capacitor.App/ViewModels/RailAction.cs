using Capacitor.App.Services;
using Lucide.Avalonia;

namespace Capacitor.App.ViewModels;

/// An action the sidebar's row menu offers. It runs over every targeted row it applies to and is left off
/// the menu when it applies to none, or, when SingleRow, whenever more than one row is targeted.
public sealed record RailAction(
    LucideIconKind Icon,
    Func<IReadOnlyList<AgentRow>, string> Label,
    Func<AgentRow, bool> AppliesTo,
    Action<IReadOnlyList<AgentRow>> Run,
    bool SingleRow = false);
