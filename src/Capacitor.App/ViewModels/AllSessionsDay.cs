using System.Collections.ObjectModel;

namespace Capacitor.App.ViewModels;

public sealed class AllSessionsDay(string label) {
    public string Label { get; } = label;
    public ObservableCollection<AllSessionsRow> Rows { get; } = [];
}
