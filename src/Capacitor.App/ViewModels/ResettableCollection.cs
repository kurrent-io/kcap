using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;

namespace Capacitor.App.ViewModels;

/// An observable list whose whole contents can be swapped with one Reset notification, so a bound
/// list lays out once per swap instead of once per item.
public sealed class ResettableCollection<T> : ObservableCollection<T> {
    public void ReplaceAll(IEnumerable<T> items) {
        CheckReentrancy();
        Items.Clear();
        foreach (var item in items) Items.Add(item);
        OnPropertyChanged(new PropertyChangedEventArgs(nameof(Count)));
        OnPropertyChanged(new PropertyChangedEventArgs("Item[]"));
        OnCollectionChanged(new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }
}
