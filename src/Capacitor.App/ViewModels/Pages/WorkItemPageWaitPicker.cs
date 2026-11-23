using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Pages;

/// The list a slice's waits are picked from: every other slice, the ones it waits on first and the ones it can't wait
/// on last, narrowed by <see cref="Filter"/>.
public sealed class WorkItemPageWaitPicker(IEnumerable<WorkItemPageWaitChoice> choices) : ReactiveObject {
    // Enough to fill the picker without scrolling; past it the search box shows.
    const int SearchFrom = 9;

    public IReadOnlyList<WorkItemPageWaitChoice> Choices { get; } = [.. choices.OrderByDescending(choice => choice.Waits).ThenBy(choice => choice.Loops)];
    public bool Searches => Choices.Count >= SearchFrom;

    string _filter = "";
    public string Filter {
        get => _filter;
        set {
            this.RaiseAndSetIfChanged(ref _filter, value);
            this.RaisePropertyChanged(nameof(Shown));
        }
    }

    public IReadOnlyList<WorkItemPageWaitChoice> Shown => Filter.Trim() is { Length: > 0 } filter
        ? [.. Choices.Where(choice => choice.Title.Contains(filter, StringComparison.OrdinalIgnoreCase))]
        : Choices;
}
