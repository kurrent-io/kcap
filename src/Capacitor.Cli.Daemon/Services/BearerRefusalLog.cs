namespace Capacitor.Cli.Daemon.Services;

/// <summary>Says once that the server refused a bearer. A connection retrying every 30 seconds presents the same one each
/// time, and a report carries the local time, so the reports themselves never read as repeats.</summary>
internal sealed class BearerRefusalLog(Action<string> warn) {
    string? _presented;
    int     _reported;

    /// <summary>Notes the bearer a connect is about to present, and passes it on.</summary>
    public string? Presenting(string? bearer) {
        if (Interlocked.Exchange(ref _presented, bearer) != bearer) Volatile.Write(ref _reported, 0);

        return bearer;
    }

    public void Refused(string report) {
        if (Interlocked.Exchange(ref _reported, 1) == 0) warn(report);
    }
}
