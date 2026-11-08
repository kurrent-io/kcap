using System.Net.Sockets;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.FirstRun;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Harness.Claude;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary>Setup's steering question, against a control socket standing in for the daemon setup just configured and
/// Claude Code's settings under a throwaway home.</summary>
public class SetupSteeringTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    const string Name = "laptop";

    List<string> Lines { get; } = [];

    string Settings => new ClaudePaths(Home, null).UserSettings;

    ClaudeSteeringMod Mod => new([(Settings, null)]);

    Task<bool> RunAsync(string[] flags, Func<bool, bool>? ask, bool offered = true, bool eligible = true) =>
        SetupSteering.RunAsync(["setup", .. flags], offered, eligible, ask, Daemons.Store, Name, TimeProvider.System, Mod, Lines.Add);

    void InstallKcap() => ClaudePluginWriter.Install(Settings, Home.PathTo("kcap-plugin"));

    FakeSteeringDaemon Daemon() => FakeSteeringDaemon.Answering(Daemons.Store.SocketPath(Name), new SteeringSetAckDto(true, null, SteeringIpc.On));

    static Func<bool, bool> Refusing => _ => throw new InvalidOperationException("the steering question was asked");

    /// <summary>Pressing Enter takes the default, and a run that can't ask takes no answer: neither asks the daemon
    /// anything or writes Claude's settings, which kcap's plugin is in, so a write would show.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Setup_offers_steering_and_leaves_it_off_by_default() {
        InstallKcap();
        var before = File.ReadAllText(Settings);
        await using var daemon = Daemon();
        var defaults = new List<bool>();

        var asked = await RunAsync([], ask: d => { defaults.Add(d); return d; });

        await Assert.That(asked).IsFalse();
        await Assert.That(defaults).IsEquivalentTo([false]);
        await Assert.That(Lines).IsEquivalentTo([SetupSteering.Explanation]);

        Lines.Clear();
        var unattended = await RunAsync([], ask: null);

        await Assert.That(unattended).IsFalse();
        await Assert.That(Lines).IsEquivalentTo([SetupSteering.LeftOff]);
        await Assert.That(daemon.Asked).IsEmpty();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Answering_yes_turns_steering_on_as_the_daemon_command_does() {
        InstallKcap();
        await using var daemon = Daemon();

        var on = await RunAsync([], ask: _ => true);

        await Assert.That(on).IsTrue();
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
        await Assert.That(Lines).Contains($"Steering is on for daemon '{Name}'.");
        await Assert.That(Lines.Any(l => l.StartsWith("Installed the kcap-steering plugin", StringComparison.Ordinal))).IsTrue();
        await Assert.That(JsonNode.Parse(File.ReadAllText(Settings))!["enabledPlugins"]![ClaudePluginInstaller.SteeringPluginKey]!.GetValue<bool>())
            .IsTrue();
    }

    /// <summary>The flag answers a run that can't ask, and is the suggested answer of one that can.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task The_flag_answers_an_unattended_run_and_suggests_the_answer_otherwise() {
        await using var daemon = Daemon();
        var defaults = new List<bool>();

        await Assert.That(await RunAsync(["--steering", "on"], ask: null)).IsTrue();
        await Assert.That(await RunAsync(["--steering", "on"], ask: d => { defaults.Add(d); return false; })).IsFalse();

        await Assert.That(defaults).IsEquivalentTo([true]);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
    }

    /// <summary>What a daemon whose switch is on has, beside its control socket: something listening on its steering
    /// socket.</summary>
    Socket SwitchedOn() {
        Daemons.CreateDir(Name);
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(Daemons.Store.SteeringSocketPath(Name)));
        socket.Listen(4);

        return socket;
    }

    FakeSteeringDaemon TurningOff() => FakeSteeringDaemon.Answering(Daemons.Store.SocketPath(Name), new SteeringSetAckDto(true, null, SteeringIpc.Off));

    /// <summary>Re-running setup is how a person updates it, and an off that setup ignored while it printed "off" would
    /// leave remote input into their terminal open with nothing saying so.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // Unix-domain sockets stand in for the daemon
    public async Task An_off_while_steering_is_on_turns_it_off() {
        using var steering = SwitchedOn();
        await using var daemon = TurningOff();

        await Assert.That(await RunAsync(["--steering", "off"], ask: null)).IsFalse();

        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.Off]);
        await Assert.That(Lines).IsEquivalentTo([$"Steering is off for daemon '{Name}'."]);
    }

    /// <summary>The question suggests the switch as it stands, and a no to it turns steering off.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // Unix-domain sockets stand in for the daemon
    public async Task The_question_suggests_steering_as_it_stands_and_a_no_turns_it_off() {
        using var steering = SwitchedOn();
        await using var daemon = TurningOff();
        var defaults = new List<bool>();

        await Assert.That(await RunAsync([], ask: d => { defaults.Add(d); return false; })).IsFalse();

        await Assert.That(defaults).IsEquivalentTo([true]);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.Off]);
    }

    /// <summary>A run that asks nothing and has no answer leaves the switch as it is, and says how it is.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // Unix-domain sockets stand in for the daemon
    public async Task A_run_with_no_answer_leaves_steering_on_and_never_calls_it_off() {
        using var steering = SwitchedOn();
        await using var daemon = TurningOff();

        await Assert.That(await RunAsync([], ask: null)).IsTrue();

        await Assert.That(daemon.Asked).IsEmpty();
        await Assert.That(Lines).IsEquivalentTo([SetupSteering.LeftOn]);
    }

    [Test]
    public async Task Steering_is_not_asked_about_where_it_could_not_reach_a_session() {
        await Assert.That(await RunAsync([], ask: Refusing, eligible: false)).IsFalse();
        await Assert.That(await RunAsync([], ask: null, eligible: false)).IsFalse();
        await Assert.That(Lines).IsEmpty();
    }

    /// <summary>A yes needs a daemon to flip the switch and kcap's Claude plugin for the steering plugin to go in beside,
    /// so neither a machine sign-in's missing service, a failed one, nor a declined plugin gets the question.</summary>
    [Test]
    public async Task Steering_is_offered_only_with_kcaps_Claude_plugin_and_a_daemon_service_up() {
        var up     = new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Enabled, null);
        var failed = new FirstRunMachineActionResult(FirstRunMachineActionOutcomes.Failed, null);

        await Assert.That(SetupSteering.Eligible(up, Mod)).IsFalse();

        InstallKcap();

        await Assert.That(SetupSteering.Eligible(up, Mod)).IsTrue();
        await Assert.That(SetupSteering.Eligible(null, Mod)).IsFalse();
        await Assert.That(SetupSteering.Eligible(failed, Mod)).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // without a daemon the flip is refused; Windows refuses before asking
    public async Task A_daemon_that_could_not_turn_it_on_leaves_steering_off_and_says_how_to_later() {
        InstallKcap();
        var before = File.ReadAllText(Settings);

        var on = await RunAsync([], ask: _ => true);

        await Assert.That(on).IsFalse();
        await Assert.That(Lines.Any(l => l.Contains($"daemon '{Name}' is not running", StringComparison.Ordinal))).IsTrue();
        await Assert.That(Lines.Any(l => l.Contains("kcap daemon steering on", StringComparison.Ordinal))).IsTrue();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    /// <summary>Through the seam rather than the OS, so it runs everywhere: no question, and the flag refused for the
    /// reason <c>kcap daemon steering on</c> gives.</summary>
    [Test]
    public async Task Setup_does_not_offer_steering_on_windows() {
        await Assert.That(await RunAsync(["--steering", "on"], ask: Refusing, offered: false)).IsFalse();
        await Assert.That(Lines).IsEmpty();
        await Assert.That(File.Exists(Settings)).IsFalse();

        await Assert.That(SetupSteering.ArgumentError(["setup", "--steering", "on"], offered: false))
            .IsEqualTo($"--steering: {DaemonSteeringCommand.NotOfferedReason}");
        await Assert.That(SetupSteering.ArgumentError(["setup", "--steering", "off"], offered: false)).IsNull();
        await Assert.That(SetupSteering.ArgumentError(["setup"], offered: false)).IsNull();
    }

    [Test]
    [Arguments("--steering")]
    [Arguments("--steering --no-prompt")]
    [Arguments("--steering maybe")]
    public async Task A_steering_flag_without_on_or_off_is_refused(string flags) =>
        await Assert.That(SetupSteering.ArgumentError(["setup", .. flags.Split(' ')], offered: true)).Contains("--steering");
}
