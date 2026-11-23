using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// install --replace --verify --activate under a polling supervisor (a Windows task or service, a systemd
/// unit): the previous profile's unit is removed and its daemon confirmed stopped before the selection
/// moves, and the selection moves before the new unit is installed.
public class PollingServiceVerifyActivateTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string NewId  = "poll-activate-new";
    const string OldId  = "poll-activate-old";
    const string TargetServer = "https://target.example.com";
    static readonly TimeSpan ShortBudget = TimeSpan.FromMilliseconds(600);

    sealed class FakeManager(Func<string?> readActive) : IServiceManager {
        public bool Registered;
        public bool Running;
        public bool StartsAnswering = true;
        public bool OldRegistered;
        public bool OldRunning;
        public IReadOnlyDictionary<string, string>? OldEnv = new Dictionary<string, string> { ["KCAP_PROFILE"] = "previous" };
        public readonly List<string> Calls = [];
        public string? ActiveAtInstall;

        public string Describe() => "fake task";
        public string UnitDirectory => "";
        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [];
        public IReadOnlyList<string> ListInstalled() => Registered ? [NewId] : [];
        public ServiceStatus Status(string serviceId) => new(ServiceState.NotInstalled, null);
        public IReadOnlyDictionary<string, string>? UnitEnvironment(string serviceId) => serviceId == OldId ? OldEnv : null;

        public bool TargetUnanswered;

        public ServiceQuery Query(string serviceId) => serviceId == NewId
            ? new(TargetUnanswered ? LabelProbe.Unknown : Registered ? LabelProbe.Loaded : LabelProbe.Absent, Registered,
                !Registered ? ServiceState.NotInstalled : Running ? ServiceState.Running : ServiceState.Installed, null, Running ? 4242 : null)
            : new(OldRegistered ? LabelProbe.Loaded : LabelProbe.Absent, OldRegistered,
                !OldRegistered ? ServiceState.NotInstalled : OldRunning ? ServiceState.Running : ServiceState.Installed, null, OldRunning ? 1111 : null);

        public bool Install(ServiceSpec spec, bool startNow, out string? error) {
            Calls.Add("install");
            ActiveAtInstall = readActive();
            Registered = true;
            Running = StartsAnswering;
            error = null;
            return true;
        }

        public void WriteAndBootstrap(ServiceSpec spec) => Install(spec, true, out _);

        public bool Uninstall(string serviceId, out string? error) {
            Calls.Add($"uninstall:{serviceId}");
            if (serviceId == NewId) Registered = Running = false;
            else OldRegistered = OldRunning = false;
            error = null;
            return true;
        }

        public bool Start(string serviceId, out string? error) { error = null; return true; }
        public bool Stop(string serviceId, out string? error) { Calls.Add($"stop:{serviceId}"); if (serviceId == NewId) Running = false; error = null; return true; }
    }

    sealed class AcquiredFence(List<string> calls) : IAdmissionFenceSession {
        public int? Pid => 1111;
        public Task<bool> CommitAsync(TimeSpan timeout, CancellationToken ct) { calls.Add("fence-commit"); return Task.FromResult(true); }
        public Task<bool> AbortAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    void SeedConfig() =>
        ConfigMutator.Mutate(Config.Root, _ => new ProfileConfig {
            ActiveProfile = "previous",
            Profiles = new() {
                ["previous"] = new Profile { ServerUrl = "https://previous.example.com", Daemon = new DaemonSettings { Name = OldId } },
                ["target"]   = new Profile { ServerUrl = TargetServer, Daemon = new DaemonSettings { Name = NewId } },
            },
        });

    string? Active() =>
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(Config.Root), out var c) ? c.ActiveProfile : "<unreadable>";

    static ServiceSpec Spec() => new(NewId, @"C:\kcap\kcap-daemon.exe", @"C:\kcap\daemon.log",
        new Dictionary<string, string> { ["KCAP_PROFILE"] = "target" }, []);

    ProfileActivation Activation() =>
        new(Config.Root, "target", TargetServer, NewId, Spec().Environment, _ => Task.CompletedTask);

    PollingServiceVerify Verify(FakeManager manager, Func<string, bool>? oldAnswers = null) =>
        new(Daemons.Store, manager,
            id => id == NewId ? (manager.Running ? 4242 : null) : manager.OldRunning ? 1111 : null,
            (id, _) => Task.FromResult(
                id == NewId && manager.Running ? new HelloProbeResult(true, HelloProtocol.CurrentVersion, null, NewId)
                : id == OldId && (manager.OldRunning || oldAnswers?.Invoke(id) == true) ? new HelloProbeResult(true, HelloProtocol.CurrentVersion, null, OldId)
                : new HelloProbeResult(false, null, null, null)),
            TimeProvider.System, () => true, ShortBudget,
            (_, _) => Task.FromResult(new AdmissionFenceAcquireResult(AdmissionFenceOutcome.Acquired, new AcquiredFence(manager.Calls))));

    [Test]
    public async Task A_cross_id_switch_retires_the_old_unit_first_then_activates_then_installs() {
        SeedConfig();
        var manager = new FakeManager(Active) { OldRegistered = true, OldRunning = true };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null,
            retireServiceId: OldId, activation: Activation(), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["fence-commit", $"uninstall:{OldId}", "install"],
            TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(manager.ActiveAtInstall).IsEqualTo("target");
    }

    [Test]
    public async Task A_same_id_switch_stops_the_current_daemon_then_activates_then_installs() {
        SeedConfig();
        var manager = new FakeManager(Active) { Registered = true, Running = true };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, activation: Activation());

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo([$"stop:{NewId}", "install"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(manager.ActiveAtInstall).IsEqualTo("target");
    }

    [Test, NotInParallel]
    public async Task A_retired_unit_pinned_to_another_profile_is_refused_untouched() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeManager(Active) { OldRegistered = true, OldRunning = true, OldEnv = new Dictionary<string, string> { ["KCAP_PROFILE"] = "target" } };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null,
            retireServiceId: OldId, activation: Activation(), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(err.GetCapturedError()).Contains("retire_reason=foreign_profile");
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test, NotInParallel]
    public async Task An_absent_retired_unit_whose_name_still_answers_is_a_live_owner() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeManager(Active);

        var exit = await Verify(manager, oldAnswers: _ => true).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null,
            retireServiceId: OldId, activation: Activation(), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(err.GetCapturedError()).Contains("retire_reason=live_owner_without_unit");
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test, NotInParallel]
    public async Task An_owner_back_under_the_retired_name_before_the_write_stops_it() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = new FakeManager(Active) { OldRegistered = true };
        var looksAfterRetire = 0;

        var exit = await Verify(manager, oldAnswers: _ => !manager.OldRegistered && looksAfterRetire++ >= 1)
            .InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: OldId, activation: Activation(), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(err.GetCapturedError()).Contains("activation_reason=activation_owner_alive");
        await Assert.That(manager.Calls).DoesNotContain("install");
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test]
    public async Task A_readiness_rollback_after_activation_keeps_the_new_selection() {
        SeedConfig();
        var manager = new FakeManager(Active) { StartsAnswering = false };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, activation: Activation());

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(Active()).IsEqualTo("target");
    }

    [Test]
    public async Task Without_activate_a_rename_still_installs_before_it_retires() {
        var manager = new FakeManager(Active) { OldRegistered = true, OldEnv = new Dictionary<string, string> { ["KCAP_PROFILE"] = "target" } };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.Calls).IsEquivalentTo(["install", $"uninstall:{OldId}"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_target_the_supervisor_cannot_read_is_refused_before_the_old_unit_is_removed() {
        SeedConfig();
        var manager = new FakeManager(Active) { OldRegistered = true, TargetUnanswered = true };

        var exit = await Verify(manager).InstallVerifiedAsync(Spec(), replace: true, expectedVersion: null,
            retireServiceId: OldId, activation: Activation(), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.Contended);
        await Assert.That(manager.Calls).DoesNotContain($"uninstall:{OldId}");
        await Assert.That(manager.OldRegistered).IsTrue();
        await Assert.That(Active()).IsEqualTo("previous");
    }
}
