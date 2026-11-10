using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>Reaches no daemon, so a source with a fresh claim stays that daemon's to finish.</summary>
public sealed class NoDaemonRecording : IDaemonRecordingClient {
    public static readonly NoDaemonRecording Instance = new();

    public Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, CancellationToken ct) => Task.FromResult(RecordingHandOver.Unanswered);

    public Task<bool> FinishSourceAsync(string key, CancellationToken ct) => Task.FromResult(false);

    public IReadOnlyList<string> Daemons() => [];

    public Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct) => Task.FromResult(new RecordingDisableAnswer(RecordingDisable.NoSocket));
}
