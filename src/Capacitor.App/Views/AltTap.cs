using Avalonia.Input;

namespace Capacitor.App.Views;

/// Alt pressed and released on its own, the Windows gesture for a window's menu. Any other key,
/// a pointer press or losing the window in between makes it a chord, not a tap. AltGr arrives as
/// Ctrl+Alt, so an Alt pressed with another modifier held never counts.
public sealed class AltTap {
    bool _armed;

    public void KeyDown(Key key, KeyModifiers modifiers) =>
        _armed = IsAlt(key) && (modifiers & ~KeyModifiers.Alt) == KeyModifiers.None;

    /// True when this release completes a tap.
    public bool KeyUp(Key key) {
        var tapped = _armed && IsAlt(key);
        _armed = false;
        return tapped;
    }

    public void Cancel() => _armed = false;

    static bool IsAlt(Key key) => key is Key.LeftAlt or Key.RightAlt;
}
