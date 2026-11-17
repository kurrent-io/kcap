namespace Capacitor.App.ViewModels;

/// A link chip: its key with the tracker's state beside it, such as "AI-5190 In Progress".
public sealed record WorkItemPageLink(string Label, string? Url, string? Title);
