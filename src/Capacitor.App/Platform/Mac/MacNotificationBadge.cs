namespace Capacitor.App.Platform.Mac;

internal sealed class MacNotificationBadge(Action<int, Action<bool>> setCount, Action<int> changed) {
    readonly HashSet<string> _waiting = new(StringComparer.Ordinal);
    bool _enabled, _writing, _dirty;
    int _applied = -1;
    int _announced;

    public int Count => _enabled ? _waiting.Count : 0;

    public void Authorize(bool enabled) { _enabled = enabled; Update(); }
    public void Delivered(string id) { _waiting.Add(id); Refresh(); }
    public void Remove(string id) { _waiting.Remove(id); Update(); }
    public void Refresh() { _dirty = true; Update(); }

    void Update() {
        Announce();
        if (_writing || _applied == Count && !_dirty) return;
        var count = Count;
        _dirty = false;
        _writing = true;
        setCount(count, success => {
            _writing = false;
            if (!success) {
                _enabled = false;
                _applied = count == 0 ? 0 : -1;
                Announce();
                if (count != 0) Update();
                return;
            }
            _applied = count;
            Update();
        });
    }

    void Announce() {
        if (_announced == Count) return;
        _announced = Count;
        changed(Count);
    }
}
