using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Recording;

namespace Capacitor.Cli;

/// <summary>What a hook or <c>kcap disable</c> asks of the daemon recording a source.</summary>
public interface IDaemonRecordingClient {
    /// <summary>Asks a daemon to record <paramref name="source"/>: the one hosting this hook's agent, or for a terminal
    /// session the one serving this hook's profile and server. Anything but <see cref="RecordingHandOver.Taken"/> leaves
    /// it to the hook.</summary>
    /// <param name="follows">The process whose exit ends a terminal session's source, when the caller knows it; the coding
    /// agent this hook runs under otherwise.</param>
    Task<RecordingHandOver> TryRecordAsync(RecordingSource source, bool skipTitle, int? follows, CancellationToken ct);

    /// <summary>Asks <paramref name="holder"/>, the daemon the source's claim names, to make the final drain of the source
    /// <paramref name="key"/> names, touching no other source. A hook that stops waiting has still asked, and the daemon
    /// still finishes.</summary>
    /// <param name="holder">Null when the claim can't be read, and the hook's own daemon is asked.</param>
    /// <param name="from">The coding-agent process the hook runs under, when it knows it. A daemon whose source followed
    /// another process that has exited reads a hook from this one as the end of a resumed run.</param>
    Task<RecordingFinish> FinishSourceAsync(string key, string? holder, ProcessIncarnation? from, CancellationToken ct);

    /// <summary>Every daemon this machine knows of, or null when that can't be read.</summary>
    IReadOnlyList<string>? Daemons();

    /// <summary>Asks <paramref name="daemonName"/> to stop every source of a session a person disabled, with no final
    /// drain.</summary>
    Task<RecordingDisableAnswer> DisableSessionAsync(string daemonName, string sessionId, CancellationToken ct);
}
