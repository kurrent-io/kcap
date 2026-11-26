using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>Each file-change item's latest changes as the app-server announced them, so its approval, which names no
/// file, can show the edit. Display only: a policy reads its paths from <see cref="CodexPatchPaths"/>, which refuses
/// what it cannot place.</summary>
internal sealed class CodexFileChanges {
    // An approval follows its item's start, so only the newest few are ever looked up.
    const int MaxItems = 16;

    readonly Lock _gate = new();
    readonly Dictionary<string, JsonElement> _changes = new(StringComparer.Ordinal);
    readonly Queue<string> _order = new();

    /// <summary>A later snapshot replaces an earlier one.</summary>
    public void Observe(string itemId, JsonElement changes) {
        var kept = changes.Clone();

        lock (_gate) {
            if (!_changes.ContainsKey(itemId)) _order.Enqueue(itemId);
            _changes[itemId] = kept;
            if (_order.Count > MaxItems) _changes.Remove(_order.Dequeue());
        }
    }

    public JsonElement? Of(string itemId) {
        lock (_gate) return _changes.TryGetValue(itemId, out var changes) ? changes : null;
    }
}
