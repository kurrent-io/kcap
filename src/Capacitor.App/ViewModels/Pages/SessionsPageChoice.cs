namespace Capacitor.App.ViewModels.Pages;

/// One option of a sessions page filter. A null Key matches every session.
public sealed record SessionsPageChoice(string? Key, string Label);
