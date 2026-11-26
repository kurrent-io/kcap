namespace Capacitor.App.ViewModels.Inbox;

/// <summary>One answer a notification offers.</summary>
public sealed record InboxButtonViewModel(string Answer, string Label, bool IsPrimary, bool IsDanger, System.Windows.Input.ICommand Command);
