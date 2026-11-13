using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services.CliAdoption;

public class AdoptionRepointTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static readonly TimeSpan Bounded = TimeSpan.FromSeconds(5);

    const string Prefix = "/opt/homebrew/lib/node_modules/@kurrent";

    static readonly OtherCliInstall Npm = new(OtherCliInstallKind.Npm, $"{Prefix}/kcap", "1.4.1-beta.2",
        $"{Prefix}/kcap/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap",
        $"{Prefix}/kcap/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap-daemon", "id", [$"{Prefix}/kcap"]);

    static readonly MutationRequest Request = new(MutationVerb.Repoint, "default", "https://cap.example.test", "daemon-a");

    static readonly DaemonActivity Idle = new(0, Busy: false, Quiet: true);

    sealed class NoObservation : IDaemonObservation {
        public Task<ObservedEvidence?> ObserveAsync(MutationRequest request, CancellationToken ct) => Task.FromResult<ObservedEvidence?>(null);
    }

    sealed record Built(AdoptionRepoint Repoint, DaemonMutationLane Lane, FakeKcapCli Cli);

    Built Build(Func<DaemonActivity?> activity, ExpectedDaemonRestart expected, FakeKcapCli? cli = null, bool classify = true) {
        cli ??= new FakeKcapCli();
        var lane = new DaemonMutationLane(Daemons.Store, new FakeLoginShellProbe(), new OutcomeChannel(),
            () => "/Applications/Kcap.app/Contents/MacOS/kcap", (_, _) => cli, _ => new NoObservation(), TimeProvider.System);
        if (!classify) lane.Classify = (_, _, _, _, _, _, _) => Task.FromResult<MutationOutcome>(new MutationOutcome.Succeeded());
        return new Built(new AdoptionRepoint(lane, activity, expected), lane, cli);
    }

    static readonly AttachStatus Down = new(AttachState.Unreachable, "daemon_unreachable", null);

    static ExpectedDaemonRestart Expected(TimeProvider? time = null) =>
        new(new BehaviorSubject<AttachStatus>(Down), new BehaviorSubject<bool>(false), () => false, () => Task.CompletedTask,
            time ?? TimeProvider.System);

    static Func<CancellationToken, Task<bool>> Same(Func<bool>? answer = null) => _ => Task.FromResult(answer?.Invoke() ?? true);

    /// <summary>Only a report that nothing is mid-work lets the move start: no report, or one that leaves the agents'
    /// turns unread, waits rather than guessing.</summary>
    [Test]
    public async Task Activity_not_quiet_never_reaches_the_lane() {
        foreach (var activity in new DaemonActivity?[] { null, new(0, Busy: false), new(2, Busy: true) }) {
            using var expected = Expected();
            var checks = 0;
            var built = Build(() => activity, expected);

            var outcome = await built.Repoint.RunAsync(Npm, Same(() => { checks++; return true; }), Request, CancellationToken.None)
                .WaitAsync(Bounded);

            await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused(AdoptionRepoint.Busy, RecoverySurface.None));
            await Assert.That(checks).IsEqualTo(0);
            await Assert.That(built.Cli.VersionCallCount).IsEqualTo(0);
            await Assert.That(built.Cli.RepointFromCalls).IsEmpty();
            await built.Lane.DisposeAsync();
        }
    }

    [Test]
    public async Task An_npm_update_since_preflight_refuses_install_changed() {
        using var expected = Expected();
        var built = Build(() => Idle, expected);
        await using var _ = built.Lane;

        var outcome = await built.Repoint.RunAsync(Npm, Same(() => false), Request, CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused(AdoptionRepoint.InstallChanged, RecoverySurface.None));
        await Assert.That(built.Cli.VersionCallCount).IsEqualTo(0);
        await Assert.That(built.Cli.RepointFromCalls).IsEmpty();
    }

    /// <summary>The repoint keeps an agent waiting on its user for the new daemon to resume, so it holds nothing.</summary>
    [Test]
    public async Task An_idle_hosted_agent_does_not_hold_the_move() {
        using var expected = Expected();
        var built = Build(() => new DaemonActivity(1, Busy: true, Quiet: true), expected, classify: false);
        await using var _ = built.Lane;

        var outcome = await built.Repoint.RunAsync(Npm, Same(), Request, CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(built.Cli.RepointFromCalls).IsEquivalentTo([Npm.Root]);
    }

    [Test]
    public async Task A_busy_repoint_maps_to_daemon_busy() {
        using var expected = Expected();
        var cli = new FakeKcapCli {
            RepointBehavior = (_, _) => Task.FromResult(new ProcessResult(3, "", "Daemon 'daemon-a' is busy, so it still runs from it.\n", false)),
        };
        var built = Build(() => Idle, expected, cli);
        await using var _ = built.Lane;

        var outcome = await built.Repoint.RunAsync(Npm, Same(), Request, CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused("daemon_busy", RecoverySurface.None));
        await Assert.That(cli.RepointFromCalls).IsEquivalentTo([Npm.Root]);
    }

    [Test]
    public async Task The_app_s_restart_recovery_is_held_while_the_repoint_runs() {
        var time = new FakeTimeProvider();
        using var expected = Expected(time);
        time.Advance(ExpectedDaemonRestart.Grace + TimeSpan.FromMinutes(1));
        var heldDuring = false;
        var cli = new FakeKcapCli {
            RepointBehavior = (_, _) => {
                heldDuring = expected.Expected;
                return Task.FromResult(new ProcessResult(0, "", "", false));
            },
        };
        var built = Build(() => Idle, expected, cli, classify: false);
        await using var _ = built.Lane;
        await Assert.That(expected.Expected).IsFalse();

        var outcome = await built.Repoint.RunAsync(Npm, Same(), Request, CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Succeeded());
        await Assert.That(cli.RepointFromCalls).IsEquivalentTo([Npm.Root]);
        await Assert.That(heldDuring).IsTrue();
        await Assert.That(expected.Expected).IsFalse();
    }

    /// <summary>A folder that is not one of the install's own could hold other installs' units.</summary>
    [Test]
    public async Task An_install_none_of_whose_folders_holds_its_daemon_is_refused_without_sending_anything() {
        var stray = Npm with { DaemonBinary = "/usr/local/libexec/kcap-daemon" };
        using var expected = Expected();
        var checks = 0;
        var built = Build(() => Idle, expected, classify: false);
        await using var _ = built.Lane;

        var outcome = await built.Repoint.RunAsync(stray, Same(() => { checks++; return true; }), Request, CancellationToken.None)
            .WaitAsync(Bounded);

        await Assert.That(outcome).IsEqualTo(new MutationOutcome.Refused(AdoptionRepoint.FolderUnknown, RecoverySurface.None));
        await Assert.That(checks).IsEqualTo(0);
        await Assert.That(built.Cli.VersionCallCount).IsEqualTo(0);
        await Assert.That(built.Cli.RepointFromCalls).IsEmpty();
    }

    /// <summary>npm can install the platform package beside the launcher rather than inside it; the folder handed to
    /// the CLI must hold the daemon a service runs, or nothing would be repointed.</summary>
    [Test]
    public async Task The_repoint_names_the_install_folder_that_holds_its_daemon() {
        var flat = Npm with {
            DaemonBinary = $"{Prefix}/kcap-darwin-arm64/bin/kcap-daemon",
            OwnedEntries = [$"{Prefix}/kcap", $"{Prefix}/kcap-darwin-arm64"],
        };
        using var expected = Expected();
        var built = Build(() => Idle, expected, classify: false);
        await using var _ = built.Lane;

        await built.Repoint.RunAsync(flat, Same(), Request, CancellationToken.None).WaitAsync(Bounded);

        await Assert.That(built.Cli.RepointFromCalls).IsEquivalentTo([$"{Prefix}/kcap-darwin-arm64"]);
    }
}
