namespace Capacitor.App.Services;

/// A main chat not yet recorded on the server, which holds every other, kept in app state so it survives a restart.
/// A choice the store fails to write still holds for this run.
public sealed class WorkItemMainSessions {
    readonly IAppStateStore _state;
    readonly Lock _lock = new();
    readonly Dictionary<string, string> _byWorkItem = new(StringComparer.Ordinal);
    readonly HashSet<string> _changedThisRun = new(StringComparer.Ordinal);

    public WorkItemMainSessions(IAppStateStore state) {
        _state = state;
        _ = LoadAsync();
    }

    public string? Get(string workItemId) {
        lock (_lock) return _byWorkItem.GetValueOrDefault(workItemId);
    }

    public void Set(string workItemId, string id) {
        lock (_lock) {
            if (_byWorkItem.GetValueOrDefault(workItemId) == id) return;
            _byWorkItem[workItemId] = id;
            _changedThisRun.Add(workItemId);
        }
        _ = _state.UpdateAsync(s => s with {
            MainSessionByWorkItem = new Dictionary<string, string>(s.MainSessionByWorkItem ?? new Dictionary<string, string>(), StringComparer.Ordinal) {
                [workItemId] = id,
            },
        });
    }

    public void Remove(string workItemId) {
        lock (_lock) {
            if (!_byWorkItem.Remove(workItemId)) return;
            _changedThisRun.Add(workItemId);
        }
        _ = _state.UpdateAsync(s => s with {
            MainSessionByWorkItem = (s.MainSessionByWorkItem ?? new Dictionary<string, string>())
                .Where(pair => pair.Key != workItemId).ToDictionary(StringComparer.Ordinal),
        });
    }

    // A choice made before the load lands is newer than the stored one.
    async Task LoadAsync() {
        var stored = (await _state.LoadAsync().ConfigureAwait(false)).MainSessionByWorkItem;
        if (stored is null) return;
        lock (_lock)
            foreach (var (workItemId, id) in stored)
                if (!_changedThisRun.Contains(workItemId)) _byWorkItem[workItemId] = id;
    }
}
