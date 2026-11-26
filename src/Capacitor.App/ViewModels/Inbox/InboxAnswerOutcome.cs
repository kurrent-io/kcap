namespace Capacitor.App.ViewModels.Inbox;

/// <summary>Whether the server took an answer, and the sentence to show either way.</summary>
public sealed record InboxAnswerOutcome(bool Accepted, string Note);
