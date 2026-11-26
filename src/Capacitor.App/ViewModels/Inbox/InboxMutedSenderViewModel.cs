namespace Capacitor.App.ViewModels.Inbox;

public sealed record InboxMutedSenderViewModel(string UserId, string Name, System.Windows.Input.ICommand UnmuteCommand);
