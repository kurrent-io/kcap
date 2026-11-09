namespace Capacitor.Cli.Recording;

/// <summary>Passes on only the first message, so a failure that repeats on every line logs once per
/// recorder.</summary>
public sealed class OnceLog(Action<string> log) {
    bool _written;

    public void Write(string message) {
        if (_written) return;

        _written = true;
        log(message);
    }
}
