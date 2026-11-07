namespace Capacitor.App.ViewModels;

/// An open chat find: its query, and the match it was on as an item index and that item's ordinal.
public sealed record ChatFindState(string Query, int ItemIndex, int Ordinal);
