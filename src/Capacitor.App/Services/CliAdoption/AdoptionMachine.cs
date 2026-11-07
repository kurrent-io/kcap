using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.CliAdoption;

/// <param name="harnesses">The registry <see cref="AdoptionConsumers.Holder"/> is given too, so a harness's files and
/// its processes are judged by one list.</param>
internal sealed class AdoptionMachine(
        AdoptionEvidenceReader evidence, IProcessRunner runner, DaemonMutationLane lane, Func<AdoptionDaemon?> daemon,
        UserHome home, HarnessRegistry harnesses, AccountStore accounts) : IAdoptionMachine {
    const string LabelPrefix = "io.kurrent.kcap.daemon.";

    readonly EntryIdentity _identity = new(runner);
    readonly Launchctl     _launchctl = new(runner);

    public Task<AdoptionEvidence> ReadEvidenceAsync(CancellationToken ct) => evidence.ReadAsync(ct);

    public Task<OtherCliInstall?> ReadInstallAsync(string binary, CancellationToken ct) =>
        OtherCliInstallReader.FromBinaryAsync(binary, _identity, ct);

    public async Task<LaunchdJob> ReadJobAsync(string unitName, CancellationToken ct) {
        var result = await _launchctl.PrintAsync(LabelPrefix + unitName, ct).ConfigureAwait(false);
        return LaunchdPrint.Classify(result.ExitCode, result.Stdout, result.Stderr) == LabelProbe.Loaded
            ? new LaunchdJob(true, LaunchdPrint.Pid(result.Stdout), LaunchdPrint.LoadedProgram(result.Stdout))
            : new LaunchdJob(false, null, null);
    }

    public string? AppDaemonName => daemon()?.Request?.DaemonName;

    public Task<MutationOutcome> RepointAsync(OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, CancellationToken ct) =>
        daemon() switch {
            null => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("no_daemon", RecoverySurface.None)),
            { Request: null } => Task.FromResult<MutationOutcome>(new MutationOutcome.Refused("no_server_configured", RecoverySurface.None)),
            { Request: { } request } own => new AdoptionRepoint(lane, own.Activity, own.Expected).RunAsync(install, stillSame, request, ct),
        };

    public Task ReattachAsync() => daemon()?.Reattach() ?? Task.CompletedTask;

    public IReadOnlyList<ProcessFact> ReadProcesses() => ProcessFacts.ForCurrentUser();

    public Task<IReadOnlyList<ReferenceSite>> ReadSitesAsync(CancellationToken ct) =>
        AdoptionReferenceSites.ReadAsync(home, harnesses, accounts, _launchctl, ct);

    public string? StartToken(int pid) => ProcessStartToken.ForPid(pid);

    public bool StillRuns(int pid, string startToken) => ProcessStartToken.Matches(pid, startToken) == true;
}
