namespace Capacitor.Cli;

/// <summary>Reaches no daemon, so a source with a fresh claim stays that daemon's to finish.</summary>
public sealed class NoDaemonRecording : IDaemonRecordingClient {
    public static readonly NoDaemonRecording Instance = new();

    public Task<bool> FinishSourceAsync(string key, CancellationToken ct) => Task.FromResult(false);
}
