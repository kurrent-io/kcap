using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>
/// install --replace --verify --activate on launchd: <c>active_profile</c> moves to the installed profile
/// only once every owner is confirmed stopped and before the new unit is written, is never rolled back,
/// and every refusal before that point leaves the selection and both units as they were.
/// </summary>
public class ServiceVerifyActivateTests {
    [TempHome] public required TempHome Home { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDir] public required TempDir Tmp { get; init; }

    const string NewId           = "svc-activate-new";
    const string OldId           = "svc-activate-old";
    const string ExpectedVersion = "1.2.3";
    const string OwnPlistContent = "<plist>own-unit</plist>";
    const string TargetServer    = "https://target.example.com";
    const string PreviousServer  = "https://previous.example.com";

    static string OldPlist(string profile) =>
        $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0"><dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.{OldId}</string>
          <key>EnvironmentVariables</key><dict><key>KCAP_PROFILE</key><string>{profile}</string></dict>
        </dict></plist>
        """;

    /// <summary>Per-id launchd state; the new id's daemon answers only once bootstrapped.</summary>
    sealed class FakeServiceManager(UserHome home) : IVerifyServiceManager {
        public string UnitPath(string serviceId) => LaunchdUnit.PlistPath(home, serviceId);
        public readonly List<string> Calls = [];
        public bool OldUnitInstalled;
        public bool OldLabelLoaded = true;
        public bool NewLoaded;
        public bool Bootstrapped;
        public bool BootstrapStarts = true;

        /// <summary>The selection on disk when the new unit was written.</summary>
        public string? ActiveAtWrite;
        public Func<string?>? ReadActive;

        public Action<string>? OnUninstall;

        public IReadOnlyList<GeneratedFile> GenerateFiles(ServiceSpec spec) => [new GeneratedFile("/fake/new.plist", OwnPlistContent)];

        public ServiceQuery Query(string serviceId, TimeSpan timeout) {
            if (serviceId == OldId)
                return !OldUnitInstalled ? new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null)
                    : OldLabelLoaded ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", 1111)
                    : new ServiceQuery(LabelProbe.Absent, true, ServiceState.Installed, "/x/kcap-daemon", null);
            return Bootstrapped || NewLoaded
                ? new ServiceQuery(LabelProbe.Loaded, true, ServiceState.Running, "/x/kcap-daemon", 4242)
                : new ServiceQuery(LabelProbe.Absent, false, ServiceState.NotInstalled, null, null);
        }

        public void WriteAndBootstrap(ServiceSpec spec, TimeSpan timeout) {
            Calls.Add($"writeAndBootstrap:{spec.ServiceId}");
            ActiveAtWrite = ReadActive?.Invoke();
            Bootstrapped = BootstrapStarts;
        }

        public bool Uninstall(string serviceId, TimeSpan timeout, out string? error) {
            Calls.Add($"uninstall:{serviceId}");
            if (serviceId == OldId) OldUnitInstalled = false; else NewLoaded = Bootstrapped = false;
            OnUninstall?.Invoke(serviceId);
            error = null;
            return true;
        }

        public bool Start(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
        public bool StartBootstrapOnly(string serviceId, TimeSpan timeout, out string? error) => Start(serviceId, timeout, out error);
        public bool Stop(string serviceId, TimeSpan timeout, out string? error) { error = null; return true; }
    }

    sealed class AcquiredFence : IAdmissionFenceSession {
        public int? Pid => 1111;
        public Task<bool> CommitAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public Task<bool> AbortAsync(TimeSpan timeout, CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    readonly List<string> _migrated = [];

    void SeedConfig(string active = "previous", string targetServer = TargetServer, string targetDaemon = NewId) =>
        ConfigMutator.Mutate(Config.Root, _ => new ProfileConfig {
            ActiveProfile = active,
            Profiles = new() {
                ["previous"] = new Profile { ServerUrl = PreviousServer, Daemon = new DaemonSettings { Name = OldId } },
                ["target"]   = new Profile { ServerUrl = targetServer, Daemon = new DaemonSettings { Name = targetDaemon } },
            },
        });

    string? Active() =>
        ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(Config.Root), out var c) ? c.ActiveProfile : "<unreadable>";

    ProfileActivation Activation(ServiceSpec spec, Func<string, Task>? migrate = null) =>
        new(Config.Root, "target", TargetServer, NewId, spec.Environment, migrate ?? (owner => {
            _migrated.Add(owner);
            return Task.CompletedTask;
        }));

    ServiceSpec Spec() =>
        new(NewId, Tmp.CreateFile("kcap-daemon"), "/x/new.log", new Dictionary<string, string> { ["KCAP_PROFILE"] = "target" }, []);

    FakeServiceManager Manager(bool oldInstalled = false) =>
        new(Home) { OldUnitInstalled = oldInstalled, ReadActive = Active };

    ServiceVerify Sut(
            FakeServiceManager manager, string? oldPlist = null, Func<string, bool>? oldAnswers = null,
            Func<string, bool>? newAnswersBeforeBoot = null, TimeSpan? forwardBudget = null) =>
        new(Daemons.Store, Config.Root, manager,
            id => id == NewId && manager.Bootstrapped ? 4242 : null,
            (id, _) => Task.FromResult(
                id == NewId && (manager.Bootstrapped || newAnswersBeforeBoot?.Invoke(id) == true) ? new HelloProbeResult(true, 1, ExpectedVersion, NewId)
                : id == OldId && oldAnswers?.Invoke(id) == true ? new HelloProbeResult(true, 1, ExpectedVersion, OldId)
                : new HelloProbeResult(false, null, null, null)),
            TimeProvider.System,
            forwardBudget: forwardBudget,
            readPlist: path => path == manager.UnitPath(OldId) ? oldPlist : OwnPlistContent,
            plistExists: path => path == manager.UnitPath(OldId) ? oldPlist is not null : true,
            acquireFence: (_, _) => Task.FromResult(new AdmissionFenceAcquireResult(AdmissionFenceOutcome.Acquired, new AcquiredFence())),
            endDaemon: (_, _) => Task.CompletedTask);

    static string[] Lines(string text) => text.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);

    [Test, NotInParallel]
    public async Task A_same_id_switch_activates_before_the_new_unit_is_written_and_says_so_on_stdout() {
        SeedConfig();
        using var output = ConsoleOutput.StartCapture();
        var manager = Manager();
        var spec = Spec();

        var exit = await Sut(manager).InstallVerifiedAsync(spec, replace: true, ExpectedVersion, activation: Activation(spec));

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.ActiveAtWrite).IsEqualTo("target");
        await Assert.That(Active()).IsEqualTo("target");
        await Assert.That(Lines(output.GetCapturedOutput())).Contains("activated=target");
        await Assert.That(_migrated).IsEquivalentTo(["previous"]);
    }

    [Test]
    public async Task A_cross_id_switch_retires_the_previous_profiles_unit_named_by_retire_profile() {
        SeedConfig();
        var manager = Manager(oldInstalled: true);
        var spec = Spec();

        var exit = await Sut(manager, OldPlist("previous"))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.OldUnitInstalled).IsFalse();
        await Assert.That(manager.Calls.IndexOf($"uninstall:{OldId}")).IsLessThan(manager.Calls.IndexOf($"writeAndBootstrap:{NewId}"));
        await Assert.That(manager.ActiveAtWrite).IsEqualTo("target");
    }

    [Test, NotInParallel]
    [Arguments("target")]
    [Arguments("other")]
    public async Task A_retired_unit_pinned_to_anything_but_the_retire_profile_is_refused_untouched(string pinned) {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager(oldInstalled: true);
        var spec = Spec();

        var exit = await Sut(manager, OldPlist(pinned))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("retire_reason=foreign_profile");
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test]
    public async Task Without_retire_profile_a_rename_still_requires_the_installed_profile() {
        var manager = Manager(oldInstalled: true);

        var exit = await Sut(manager, OldPlist("target"))
            .InstallVerifiedAsync(Spec(), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
        await Assert.That(manager.OldUnitInstalled).IsFalse();
    }

    [Test, NotInParallel]
    [Arguments("https://elsewhere.example.com", NewId, "expect_server_mismatch")]
    [Arguments(TargetServer, "some-other-daemon", "daemon_name_mismatch")]
    public async Task A_target_that_would_not_serve_the_expected_identity_is_refused_before_anything_is_touched(
            string targetServer, string targetDaemon, string reason) {
        SeedConfig(targetServer: targetServer, targetDaemon: targetDaemon);
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager(oldInstalled: true);
        var spec = Spec();

        var exit = await Sut(manager, OldPlist("previous"))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(Lines(err.GetCapturedError())).IsEquivalentTo([$"activation_reason={reason}", "verify_activation_refused"]);
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(_migrated).IsEmpty();
        await Assert.That(Active()).IsEqualTo("previous");
        await Assert.That(ServiceTxnMarker.Exists(Daemons.Store, NewId)).IsFalse();
    }

    [Test, NotInParallel]
    public async Task A_config_that_cannot_be_read_is_refused_before_anything_is_touched() {
        File.WriteAllText(AppConfig.GetConfigPath(Config.Root), "{ not json");
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager(oldInstalled: true);
        var spec = Spec();

        var exit = await Sut(manager, OldPlist("previous"))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("activation_reason=activation_config_unreadable");
        await Assert.That(manager.Calls).IsEmpty();
    }

    [Test, NotInParallel]
    public async Task A_migration_that_throws_is_refused_before_anything_is_touched() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager(oldInstalled: true);
        var spec = Spec();

        var exit = await Sut(manager, OldPlist("previous"))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId,
                activation: Activation(spec, _ => throw new IOException("disk full")), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("activation_reason=activation_migration_failed");
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(manager.OldUnitInstalled).IsTrue();
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test, NotInParallel]
    public async Task An_absent_retired_unit_whose_name_still_answers_is_a_live_owner_and_nothing_is_touched() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager();
        var spec = Spec();

        var exit = await Sut(manager, oldPlist: null, oldAnswers: _ => true)
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.RetireRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("retire_reason=live_owner_without_unit");
        await Assert.That(manager.Calls).IsEmpty();
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test]
    public async Task Without_activate_an_absent_retired_unit_is_still_a_no_op_whatever_answers() {
        var manager = Manager();

        var exit = await Sut(manager, oldPlist: null, oldAnswers: _ => true)
            .InstallVerifiedAsync(Spec(), replace: true, ExpectedVersion, retireServiceId: OldId);

        await Assert.That(exit).IsEqualTo(VerifyExit.Ok);
    }

    [Test, NotInParallel]
    public async Task An_owner_that_answers_under_the_retired_name_after_the_matrix_stops_the_write() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager(oldInstalled: true);
        var spec = Spec();
        // The retire's own stop confirmation finds the old daemon gone; the check before the write finds it back.
        var looksAfterRetire = 0;

        var exit = await Sut(manager, OldPlist("previous"), oldAnswers: _ => !manager.OldUnitInstalled && looksAfterRetire++ >= 1)
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("activation_reason=activation_owner_alive");
        await Assert.That(manager.Calls).DoesNotContain($"writeAndBootstrap:{NewId}");
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test, NotInParallel]
    public async Task An_owner_that_answers_under_the_target_name_after_the_matrix_stops_the_write() {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager();
        var spec = Spec();

        var exit = await Sut(manager, newAnswersBeforeBoot: _ => true)
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, activation: Activation(spec));

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("activation_reason=activation_owner_alive");
        await Assert.That(manager.Calls).DoesNotContain($"writeAndBootstrap:{NewId}");
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test, NotInParallel]
    [Arguments("removed")]
    [Arguments("repointed")]
    [Arguments("renamed")]
    public async Task A_target_changed_after_viability_is_stale_and_nothing_is_written_for_the_new_unit(string change) {
        SeedConfig();
        using var err = ConsoleOutput.StartErrorCapture();
        var manager = Manager(oldInstalled: true);
        manager.OnUninstall = id => {
            if (id != OldId) return;
            ConfigMutator.Mutate(Config.Root, c => {
                var profiles = new Dictionary<string, Profile>(c.Profiles);
                var target = profiles["target"];
                if (change == "removed") profiles.Remove("target");
                else profiles["target"] = change == "repointed"
                    ? target with { ServerUrl = "https://moved.example.com" }
                    : target with { Daemon = new DaemonSettings { Name = "renamed-daemon" } };
                return c with { Profiles = profiles };
            });
        };
        var spec = Spec();

        var exit = await Sut(manager, OldPlist("previous"))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, retireServiceId: OldId, activation: Activation(spec), retireProfile: "previous");

        await Assert.That(exit).IsEqualTo(VerifyExit.ActivationRefused);
        await Assert.That(Lines(err.GetCapturedError())).Contains("activation_reason=activation_stale");
        await Assert.That(manager.Calls).DoesNotContain($"writeAndBootstrap:{NewId}");
        await Assert.That(Active()).IsEqualTo("previous");
    }

    [Test]
    public async Task A_readiness_rollback_after_activation_keeps_the_new_selection() {
        SeedConfig();
        var manager = Manager();
        manager.BootstrapStarts = false;
        var spec = Spec();

        var exit = await Sut(manager, forwardBudget: TimeSpan.FromMilliseconds(600))
            .InstallVerifiedAsync(spec, replace: true, ExpectedVersion, activation: Activation(spec));

        await Assert.That(exit).IsEqualTo(VerifyExit.ReadinessTimeout);
        await Assert.That(Active()).IsEqualTo("target");
    }

    [Test]
    public async Task Activation_without_replace_is_a_programming_error() {
        var spec = Spec();

        await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut(Manager()).InstallVerifiedAsync(spec, replace: false, ExpectedVersion, activation: Activation(spec)));
    }
}
