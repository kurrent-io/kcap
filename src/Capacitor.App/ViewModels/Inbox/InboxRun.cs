namespace Capacitor.App.ViewModels.Inbox;

/// <summary>One styled span of a notification's text. Url is absolute and openable, or null.</summary>
public sealed record InboxRun(string Text, InboxRunStyle Style, string? Url);
