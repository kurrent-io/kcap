using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One row of the rail's harness filter. Checking or unchecking asks the rail, which writes the
/// result back through Sync, so the row never shows a state the filter does not hold.
public sealed class RailHarnessOption : ReactiveObject, IDisposable {
    readonly Action<string, bool> _setShown;

    public RailHarnessOption(string vendor, string label, Action<string, bool> setShown, Action<string> showOnly) {
        Vendor = vendor;
        Label = label;
        _setShown = setShown;
        OnlyCommand = ReactiveCommand.Create(() => showOnly(vendor));
    }

    public string Vendor { get; }
    public string Label { get; }
    public string OnlyAccessibleName => $"Show only {Label}";

    bool _isChecked = true;
    public bool IsChecked {
        get => _isChecked;
        set {
            if (_isChecked != value) _setShown(Vendor, value);
        }
    }

    internal void Sync(bool isChecked) => this.RaiseAndSetIfChanged(ref _isChecked, isChecked, nameof(IsChecked));

    int _count;
    /// Sessions of this harness the scope and state controls admit, whatever the harness filter says.
    public int Count {
        get => _count;
        internal set => this.RaiseAndSetIfChanged(ref _count, value);
    }

    public ReactiveCommand<Unit, Unit> OnlyCommand { get; }

    public void Dispose() => OnlyCommand.Dispose();
}
