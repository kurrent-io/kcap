namespace Capacitor.App.ViewModels.Inbox;

/// <summary>One answer a notification offers. Detail says what picking it means, beyond its label.</summary>
public sealed record InboxButtonViewModel(string Answer, string Label, string? Detail, bool IsPrimary, bool IsDanger, System.Windows.Input.ICommand Command);
