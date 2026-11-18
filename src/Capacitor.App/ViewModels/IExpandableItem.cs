namespace Capacitor.App.ViewModels;

/// A chat item whose body folds, for the shared expand chevron.
public interface IExpandableItem {
    bool IsExpanded { get; }
}
