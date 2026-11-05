using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services.CliAdoption;

/// <summary>Moves an install's daemon services onto the bundle's daemon through the mutation lane, and only at a safe
/// point: the daemon reports nothing in flight, and the install is still the one preflight recorded. A daemon that
/// cannot say whether it is busy counts as busy. The app's restart recovery is held for the whole request, so it
/// does not start a daemon of its own while the service is between binaries.</summary>
internal sealed class AdoptionRepoint(DaemonMutationLane lane, Func<DaemonActivity?> activity, ExpectedDaemonRestart expected) {
    public const string Busy           = "busy";
    public const string InstallChanged = "install_changed";
    public const string FolderUnknown  = "install_folder_unknown";

    /// <param name="stillSame">Whether the install's identity, its version and the job's pid are still what was
    /// recorded, asked right before the request.</param>
    /// <param name="request">Names the daemon whose successor proves the move; its verb and folder are set here.</param>
    public async Task<MutationOutcome> RunAsync(
            OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, MutationRequest request, CancellationToken ct) {
        if (DaemonFolder(install) is not { } folder) return new MutationOutcome.Refused(FolderUnknown, RecoverySurface.None);
        if (activity() is not { Busy: false }) return new MutationOutcome.Refused(Busy, RecoverySurface.None);
        if (!await stillSame(ct).ConfigureAwait(false)) return new MutationOutcome.Refused(InstallChanged, RecoverySurface.None);

        using (expected.HoldForUpdate()) {
            return await lane.RunAsync(request with { Verb = MutationVerb.Repoint, RepointFrom = folder }, ct)
                .ConfigureAwait(false);
        }
    }

    // npm may put the platform package, which holds the daemon, beside the launcher rather than inside it. Any other
    // folder could hold units of installs that are not this one.
    static string? DaemonFolder(OtherCliInstall install) =>
        install.OwnedEntries.Prepend(install.Root).FirstOrDefault(entry => CanonicalPath.IsWithin(install.DaemonBinary, entry));
}
