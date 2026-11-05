using System.Text;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>The last lines a process wrote to stderr, bounded. Appended from the reader task and read after a
/// kill that may have left that task running, so access is locked.</summary>
sealed class StderrTail(int maxChars = 8_000) {
    readonly StringBuilder _text = new();
    readonly Lock          _gate = new();

    public void Append(string line) {
        lock (_gate) {
            if (_text.Length > 0) _text.Append('\n');
            _text.Append(line);
            if (_text.Length > maxChars) _text.Remove(0, _text.Length - maxChars);
        }
    }

    public string Text {
        get {
            lock (_gate) return _text.ToString();
        }
    }
}
