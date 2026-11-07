using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>What a hook or <c>kcap disable</c> asks of the daemon recording a source.</summary>
public interface IDaemonRecordingClient {
    /// <summary>Asks the daemon hosting this hook's agent to record <paramref name="source"/>. Anything but
    /// <see cref="RecordingHandOver.Taken"/> leaves it to the hook.</summary>
    Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, CancellationToken ct);

    /// <summary>Asks the daemon to make the final drain of the source <paramref name="key"/> names, touching no other
    /// source. Answers whether it finished; a hook that stops waiting has still asked, and the daemon still finishes.</summary>
    Task<bool> FinishSourceAsync(string key, CancellationToken ct);

    /// <summary>Every daemon this machine knows of, or null when that can't be read.</summary>
    IReadOnlyList<string>? Daemons();

    /// <summary>Asks <paramref name="daemonName"/> to stop every source of a session a person disabled, with no final
    /// drain.</summary>
    Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct);
}
