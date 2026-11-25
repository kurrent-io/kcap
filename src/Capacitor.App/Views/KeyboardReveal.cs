using Avalonia.Input;
using Avalonia.Interactivity;

namespace Capacitor.App.Views;

/// Whether keyboard use, rather than a click, put focus inside a control, so actions it reveals stay
/// visible for a keyboard user. A click hands them to the pointer, even on a control that already has
/// focus, and a key press takes them back.
sealed class KeyboardReveal {
    readonly InputElement _owner;
    readonly Action _changed;

    public KeyboardReveal(InputElement owner, Action changed) {
        _owner = owner;
        _changed = changed;
        owner.AddHandler(InputElement.GotFocusEvent, OnGotFocus, RoutingStrategies.Bubble, handledEventsToo: true);
        owner.AddHandler(InputElement.PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel, handledEventsToo: true);
        owner.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel, handledEventsToo: true);
    }

    public bool IsOn { get; private set; }

    /// For the owner to call when focus has left it for good, rather than into something it opened.
    public void FocusLeft() => Set(false);

    // Focus handed back without a method, as a menu does on closing, keeps the state it found.
    void OnGotFocus(object? sender, FocusChangedEventArgs e) {
        switch (e.NavigationMethod) {
            case NavigationMethod.Tab or NavigationMethod.Directional:
                Set(true);
                break;
            case NavigationMethod.Pointer:
                Set(false);
                break;
        }
    }

    // A click on a control that already has focus raises no focus change, so the press is caught here.
    void OnPointerPressed(object? sender, PointerPressedEventArgs e) => Set(false);

    void OnKeyDown(object? sender, KeyEventArgs e) {
        if (_owner.IsKeyboardFocusWithin) Set(true);
    }

    void Set(bool value) {
        if (IsOn == value) return;
        IsOn = value;
        _changed();
    }
}
