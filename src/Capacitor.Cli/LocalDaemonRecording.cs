using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>Reaches the daemon hosting this hook's agent over local control. Only that daemon has a say: the agent's
/// environment names it, so a hook on another profile can't reach the wrong one. A disable goes to the daemon its caller
/// names instead, because it runs from a terminal as often as from an agent and a claim says who holds a source.</summary>
/// <param name="daemonName">What the daemon put in its agent's environment; null outside a hosted agent.</param>
public sealed class LocalDaemonRecording(DaemonStore store, HostedAgent hosted, string? daemonName) : IDaemonRecordingClient {
    public Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, CancellationToken ct) {
        if (hosted.AgentId is not { } agentId || daemonName is not { Length: > 0 } name || source.Vendor != "claude") return Task.FromResult(RecordingHandOver.Unanswered);

        return RecordingIpc.RecordAsync(store, name, new RecordSourceDto(
            agentId, source.SessionId, source.AgentId, source.TranscriptPath, source.Vendor, source.Cwd, skipTitle), ct);
    }

    public Task<bool> FinishSourceAsync(string key, CancellationToken ct) =>
        daemonName is { Length: > 0 } name ? RecordingIpc.FinishAsync(store, name, key, ct) : Task.FromResult(false);

    public Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct) =>
        RecordingIpc.DisableAsync(store, daemonName, sessionId, ct);
}
