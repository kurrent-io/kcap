using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.ViewModels.NextWork;

namespace Capacitor.App.Views;

/// <summary>Holds the band still while the pointer or keyboard focus is inside it: a refresh that lands meanwhile
/// waits until both have left.</summary>
public partial class NextWorkBandView : UserControl {
    bool _pointerPinned;
    bool _focusPinned;

    public NextWorkBandView() => InitializeComponent();

    NextWorkBandViewModel? Band => DataContext as NextWorkBandViewModel;

    void OnPointerEntered(object? sender, PointerEventArgs e) {
        if (_pointerPinned) return;
        _pointerPinned = true;
        Band?.Pin();
    }

    void OnPointerExited(object? sender, PointerEventArgs e) {
        if (!_pointerPinned) return;
        _pointerPinned = false;
        Band?.Release();
    }

    void OnGotFocus(object? sender, FocusChangedEventArgs e) {
        if (_focusPinned) return;
        _focusPinned = true;
        Band?.Pin();
    }

    void OnLostFocus(object? sender, FocusChangedEventArgs e) {
        if (!_focusPinned || IsKeyboardFocusWithin) return;
        _focusPinned = false;
        Band?.Release();
    }

    protected override void OnDataContextChanged(EventArgs e) {
        // A pin taken against the previous band is not this one's to release.
        _pointerPinned = false;
        _focusPinned = false;
        base.OnDataContextChanged(e);
    }
}
