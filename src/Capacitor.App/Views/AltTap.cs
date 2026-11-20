using Avalonia.Input;

namespace Capacitor.App.Views;

/// Alt pressed and released on its own, the Windows gesture for a window's menu. Any other key,
/// a pointer press or losing the window in between makes it a chord, not a tap. AltGr arrives as
/// Ctrl+Alt, so an Alt pressed with another modifier held never counts.
public sealed class AltTap {
    bool _armed;
    // Held Alt auto-repeats its KeyDown; only the first press of a hold may arm, or a repeat would
    // rearm a hold that a chord already cancelled.
    bool _held;

    public void KeyDown(Key key, KeyModifiers modifiers) {
        if (!IsAlt(key)) {
            _armed = false;
            return;
        }
        if (!_held) _armed = (modifiers & ~KeyModifiers.Alt) == KeyModifiers.None;
        _held = true;
    }

    /// True when this release completes a tap.
    public bool KeyUp(Key key) {
        var tapped = _armed && IsAlt(key);
        _armed = false;
        if (IsAlt(key)) _held = false;
        return tapped;
    }

    public void Cancel() => _armed = false;

    /// Forgets the hold too, for when the release may arrive at another window.
    public void Reset() => _armed = _held = false;

    static bool IsAlt(Key key) => key is Key.LeftAlt or Key.RightAlt;
}
