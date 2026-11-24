using System.Security.Cryptography;
using System.Text.Json;
using Avalonia.Threading;

namespace Capacitor.App.Services;

/// The desktop UI owns the debounce and its in-memory positions, including writes the disk refused.
public sealed partial class ChatReadingPositions(IAppStateStore store, string serverProfile, IObservable<string?> subjects) {
    internal const int Capacity = 128;
    readonly OrderedDictionary<string, ChatReadingPosition?> _pending = new(StringComparer.Ordinal);
    OrderedDictionary<string, ChatReadingPosition>? _positions;
    Task? _opening;
    Task _writing = Task.CompletedTask;
    DispatcherTimer? _debounce;

    internal volatile bool PreviewEnabled = true;

    internal IObservable<string?> Subjects => subjects;

    internal string Key(string subject, AgentOrigin origin, string session, string feed) {
        var parts = JsonSerializer.SerializeToUtf8Bytes(new[] { serverProfile, subject, origin.ToString(), session, feed },
            ReadingPositionJsonContext.Default.StringArray);
        return Convert.ToHexString(SHA256.HashData(parts));
    }

    internal async Task<ChatReadingPosition?> ReadAsync(string key) {
        await (_opening ??= LoadAsync());
        return _positions!.GetValueOrDefault(key) is { IsValid: true } position ? position : null;
    }

    async Task LoadAsync() {
        var saved = (await store.LoadAsync()).ChatReadingPositions;
        _positions = Opening(saved);
        foreach (var entry in _pending) Apply(_positions, entry.Key, entry.Value);
    }

    internal void Remember(string key, ChatReadingPosition? position) {
        if (_positions is { } positions) {
            if (Equals(positions.GetValueOrDefault(key), position)) return;
            Apply(positions, key, position);
        }
        _pending.Remove(key);
        _pending.Add(key, position);
        while (_pending.Count > Capacity) _pending.RemoveAt(0);
        if (_debounce is null) {
            _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            _debounce.Tick += (_, _) => _ = FlushAsync();
        }
        _debounce.Stop();
        _debounce.Start();
    }

    internal Task FlushAsync() {
        _debounce?.Stop();
        if (_pending.Count == 0) return _writing;
        return _writing = WriteAsync(_writing);
    }

    async Task WriteAsync(Task previous) {
        await previous;
        if (_pending.Count == 0) return;
        var batch = _pending.ToArray();
        if (!await store.UpdateAsync(state => {
            var updated = Opening(state.ChatReadingPositions);
            foreach (var entry in batch) Apply(updated, entry.Key, entry.Value);
            return state with { ChatReadingPositions = updated.ToDictionary() };
        })) return;

        foreach (var (key, position) in batch)
            if (_pending.TryGetValue(key, out var current) && Equals(current, position)) _pending.Remove(key);
    }

    static OrderedDictionary<string, ChatReadingPosition> Opening(IReadOnlyDictionary<string, ChatReadingPosition>? saved) {
        var entries = new OrderedDictionary<string, ChatReadingPosition>(StringComparer.Ordinal);
        if (saved is not null)
            foreach (var (key, position) in saved.TakeLast(Capacity))
                if (position is { IsValid: true }) entries.Add(key, position);
        return entries;
    }

    static void Apply(OrderedDictionary<string, ChatReadingPosition> positions, string key, ChatReadingPosition? position) {
        // Remove before adding so the persisted order reflects the last reading action.
        positions.Remove(key);
        if (position is not null) positions.Add(key, position);
        while (positions.Count > Capacity) positions.RemoveAt(0);
    }

    [System.Text.Json.Serialization.JsonSerializable(typeof(string[]))]
    sealed partial class ReadingPositionJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
}
