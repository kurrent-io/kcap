using Capacitor.App.Services.Mutation;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>What the takeover reads from this Mac and asks of it beyond its own record and the files it moves:
/// launchd, the app's daemon through the mutation lane, and the processes and files that may still use an install.</summary>
internal interface IAdoptionMachine {
    Task<AdoptionEvidence> ReadEvidenceAsync(CancellationToken ct);

    /// <summary>The install <paramref name="binary"/> belongs to as it is now; null when it is gone or unreadable.</summary>
    Task<OtherCliInstall?> ReadInstallAsync(string binary, CancellationToken ct);

    /// <summary>Throws <see cref="IOException"/> when launchd's answer cannot be read.</summary>
    Task<LaunchdJob> ReadJobAsync(string unitName, CancellationToken ct);

    /// <summary>The daemon whose successor a repoint proves; null before the app's daemon graph exists.</summary>
    string? AppDaemonName { get; }

    Task<MutationOutcome> RepointAsync(OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, CancellationToken ct);

    /// <summary>Has the app's daemon client attach now rather than when its backoff next allows.</summary>
    Task ReattachAsync();

    IReadOnlyList<ProcessFact> ReadProcesses();

    Task<IReadOnlyList<ReferenceSite>> ReadSitesAsync(CancellationToken ct);

    string? StartToken(int pid);

    /// <summary>Whether <paramref name="pid"/> is still the process that had <paramref name="startToken"/>.</summary>
    bool StillRuns(int pid, string startToken);

    /// <summary>Returns once <paramref name="pid"/> is no longer the process that had <paramref name="startToken"/>, or
    /// after <paramref name="bound"/>. Without a token nothing tells the process apart from a later one with its pid, so
    /// the whole bound passes.</summary>
    Task WaitForExitAsync(int pid, string? startToken, TimeSpan bound, CancellationToken ct);
}
