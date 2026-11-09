namespace Capacitor.App.ViewModels;

/// The <paramref name="Ordinal"/>th occurrence of the query inside one transcript item.
public sealed record ChatFindMatch(ChatItemViewModel Item, int ItemIndex, int Ordinal);
