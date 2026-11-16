namespace Capacitor.App.Services;

/// <summary>The longest stretch one hub stayed connected, across its in-place reconnects. Time spent reconnecting is not
/// connected time, so an outage between two short connections never adds up to a stable one.</summary>
sealed class ConnectedSpans(TimeProvider time) {
    readonly Lock _lock = new();
    long          _upSince;
    TimeSpan      _longest;

    public TimeSpan Longest { get { lock (_lock) return _longest; } }

    public void Up() {
        lock (_lock) _upSince = time.GetTimestamp();
    }

    public void Down() {
        lock (_lock) {
            if (_upSince == 0) return;

            var span = time.GetElapsedTime(_upSince);
            if (span > _longest) _longest = span;
            _upSince = 0;
        }
    }
}
