namespace Capacitor.App.Platform;

/// A request raised with nobody listening is kept and handed to the first listener, on the thread that adds it.
public sealed class HeldRequest {
    readonly Lock _gate = new();
    Action? _listeners;
    bool _held;

    public void Listen(Action? listener) {
        var deliver = false;
        lock (_gate) {
            _listeners += listener;
            if (listener is not null) {
                deliver = _held;
                _held = false;
            }
        }
        if (deliver) listener!.Invoke();
    }

    public void Ignore(Action? listener) {
        lock (_gate) _listeners -= listener;
    }

    public void Raise() {
        Action? listeners;
        lock (_gate) {
            listeners = _listeners;
            _held = listeners is null;
        }
        listeners?.Invoke();
    }
}
