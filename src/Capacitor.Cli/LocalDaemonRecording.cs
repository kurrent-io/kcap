using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>Reaches a daemon over local control. A hosted agent's hook asks the daemon its environment names, so it can't
/// reach the wrong one. A terminal hook asks the daemon its profile names, which takes the session only if the profile,
/// server and directories the hook resolved are its own. A finish goes to the daemon the claim names, and a disable to the
/// one its caller names, because it runs from a terminal as often as from an agent.</summary>
/// <param name="daemonName">What the daemon put in its agent's environment; null outside a hosted agent.</param>
/// <param name="terminal">How a hook outside any hosted agent hands a session over; null hands none over.</param>
public sealed class LocalDaemonRecording(DaemonStore store, HostedAgent hosted, string? daemonName, TerminalHandOff? terminal = null) : IDaemonRecordingClient {
    public Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, int? follows, CancellationToken ct) {
        if (source.Vendor != "claude") return Task.FromResult(RecordingHandOver.Unanswered);

        if (hosted.AgentId is { } agentId) {
            return daemonName is { Length: > 0 } name
                ? RecordingIpc.RecordAsync(store, name, new RecordSourceDto(agentId, source.SessionId, source.AgentId, source.TranscriptPath, source.Vendor, source.Cwd, skipTitle), ct)
                : Task.FromResult(RecordingHandOver.Unanswered);
        }

        // A hosted run that names no agent isn't a terminal session, and gets a watcher like any source no daemon takes.
        if (hosted.IsHosted || terminal is null) return Task.FromResult(RecordingHandOver.Unanswered);

        return RecordingIpc.RecordTerminalAsync(store, terminal.DaemonName, () => terminal.Describe(source, skipTitle, follows), ct);
    }

    public Task<RecordingFinish> FinishSourceAsync(string key, string? holder, CancellationToken ct) =>
        (holder ?? OwnDaemon) is { Length: > 0 } name ? RecordingIpc.FinishAsync(store, name, key, ct) : Task.FromResult(RecordingFinish.Unanswered);

    string? OwnDaemon => hosted.AgentId is not null ? daemonName : terminal?.DaemonName;

    public IReadOnlyList<string>? Daemons() {
        try {
            return store.EnumerateNames();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct) =>
        RecordingIpc.DisableAsync(store, daemonName, sessionId, ct);
}
