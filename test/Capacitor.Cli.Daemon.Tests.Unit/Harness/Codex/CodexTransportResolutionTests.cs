using System.Collections.Frozen;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Daemon.Harness.Codex;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Codex;

public class CodexTransportResolutionTests {
    [Test]
    public async Task Resolve_does_not_probe_when_the_switch_is_off() {
        var probes = 0;

        var resolution = CodexTransportResolution.Resolve(3, switchOn: false, () => { probes++; return "0.146.0"; });

        await Assert.That(probes).IsEqualTo(0);
        await Assert.That(resolution).IsEqualTo(new CodexTransportResolution(3, false, null, false, null));
        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.Pty);
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_is_active_at_the_floor() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.146.0");

        await Assert.That(resolution).IsEqualTo(new CodexTransportResolution(1, true, "0.146.0", true, null));
        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.AppServer);
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexAppServerLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_is_inactive_below_the_floor_with_a_reason() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: true, () => "0.145.9");

        await Assert.That(resolution.Active).IsFalse();
        await Assert.That(resolution.CliVersion).IsEqualTo("0.145.9");
        await Assert.That(resolution.InactiveReason).IsEqualTo("Codex 0.145.9 is below 0.146.0");
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_is_inactive_when_the_version_is_unreadable() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: true, () => null);

        await Assert.That(resolution.Active).IsFalse();
        await Assert.That(resolution.InactiveReason).IsEqualTo("Codex version could not be read");
    }

    [Test]
    public async Task Resolve_by_default_probes_and_takes_app_server_with_the_switch_unset() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: false, () => "0.154.0", byDefault: true);

        await Assert.That(resolution).IsEqualTo(new CodexTransportResolution(1, false, "0.154.0", true, null, ByDefault: true));
        await Assert.That(resolution.Probed).IsTrue();
        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.AppServer);
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexAppServerLauncherPolicyVersion);
    }

    [Test]
    public async Task Resolve_by_default_stays_on_pty_below_the_floor() {
        var resolution = CodexTransportResolution.Resolve(1, switchOn: false, () => "0.145.9", byDefault: true);

        await Assert.That(resolution.Active).IsFalse();
        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.Pty);
        await Assert.That(resolution.LauncherPolicyVersion).IsEqualTo(DaemonRunner.CodexLauncherPolicyVersion);
    }

    /// <summary>The platform default stands in only for a switch the profile never set: an explicit
    /// disable wins, and is not even probed.</summary>
    [Test]
    [Arguments(true, null, true)]
    [Arguments(true, false, false)]
    [Arguments(true, true, true)]
    [Arguments(false, null, false)]
    [Arguments(false, true, true)]
    public async Task Read_takes_the_platform_default_only_for_an_unset_switch(bool platformDefault, bool? saved, bool active) {
        var read = saved is { } on
            ? new ExperimentalSettingsRead(true, new Dictionary<string, bool> { ["codex-app-server"] = on }.ToFrozenDictionary(), null)
            : ExperimentalSettingsRead.Empty;
        var probes = 0;

        var resolution = CodexTransportResolution.Read(
            1, () => read, () => { probes++; return "0.154.0"; }, _ => throw new InvalidOperationException("unreachable"), platformDefault);

        await Assert.That(resolution.Active).IsEqualTo(active);
        await Assert.That(resolution.SwitchOn).IsEqualTo(saved == true);
        await Assert.That(probes > 0).IsEqualTo(active);
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task On_windows_an_unset_switch_takes_app_server() {
        var resolution = CodexTransportResolution.Read(
            1, () => ExperimentalSettingsRead.Empty, () => "0.154.0", _ => throw new InvalidOperationException("unreachable"),
            CodexTransportDecision.AppServerByDefault);

        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.AppServer);
        await Assert.That(resolution.ByDefault).IsTrue();
    }

    [Test]
    [ExcludeOn(OS.Windows)]
    public async Task Elsewhere_an_unset_switch_stays_on_pty() {
        var probes = 0;

        var resolution = CodexTransportResolution.Read(
            1, () => ExperimentalSettingsRead.Empty, () => { probes++; return "0.154.0"; },
            _ => throw new InvalidOperationException("unreachable"), CodexTransportDecision.AppServerByDefault);

        await Assert.That(resolution.InteractiveTransport).IsEqualTo(CodexTransportDecision.Pty);
        await Assert.That(probes).IsEqualTo(0);
    }

    static readonly ExperimentalSettingsRead On =
        new(true, new Dictionary<string, bool> { ["codex-app-server"] = true }.ToFrozenDictionary(), null);

    static readonly ExperimentalSettingsRead Broken = new(false, FrozenDictionary<string, bool>.Empty, "experimental.json is not valid JSON");

    static readonly CodexTransportResolution Kept = new(4, true, "0.146.0", true, null);

    /// <summary>Whichever read fails, before the probe or after it, the pass answers the fallback and
    /// never a switch-off resolution.</summary>
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    public async Task Read_answers_the_fallback_when_a_read_fails(int failingRead) {
        var reads = 0;
        string? problem = null;

        var resolution = CodexTransportResolution.Read(5, () => reads++ == failingRead ? Broken : On, () => "0.146.0",
            p => { problem = p; return Kept; });

        await Assert.That(ReferenceEquals(resolution, Kept)).IsTrue();
        await Assert.That(problem).IsEqualTo("experimental.json is not valid JSON");
    }
}
