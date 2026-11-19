using System.Collections.ObjectModel;

namespace Capacitor.App.ViewModels.Pages;

public sealed class SessionsPageDay(string label) {
    public string Label { get; } = label;
    public ObservableCollection<SessionsPageRow> Rows { get; } = [];
}
