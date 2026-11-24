using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

internal sealed record ChatTurnOutlineEntry(ChatTurnDestination? Destination, ChatTurnOutlineEntry.Action Command = default) {
    internal enum Action { Jump, First, Latest, LoadEarlier }

    public string Title => Command switch {
        Action.First => "First loaded turn",
        Action.Latest => "Latest loaded turn",
        Action.LoadEarlier => "Load earlier history",
        _ => Destination!.Title,
    };

    public string Detail => Destination?.Detail ?? "";
    public string AccessibleName => Detail is { Length: > 0 } detail ? $"{Title}. {detail}" : Title;
}
