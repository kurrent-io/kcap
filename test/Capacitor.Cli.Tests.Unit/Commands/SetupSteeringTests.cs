using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
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

    Task<bool> RunAsync(string[] flags, Func<bool, bool>? ask, bool offered = true, bool claudeDetected = true) =>
        SetupSteering.RunAsync(
            ["setup", .. flags], offered, claudeDetected, ask, Daemons.Store, Name, TimeProvider.System,
            new ClaudeSteeringMod(Settings, () => Home.PathTo("kcap-plugin")), Lines.Add);

    FakeSteeringDaemon Daemon() => FakeSteeringDaemon.Answering(Daemons.Store.SocketPath(Name), new SteeringSetAckDto(true, null, SteeringIpc.On));

    static Func<bool, bool> Refusing => _ => throw new InvalidOperationException("the steering question was asked");

    /// <summary>Pressing Enter takes the default, and a run that can't ask takes no answer: neither asks the daemon
    /// anything or writes Claude's settings.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Setup_offers_steering_and_leaves_it_off_by_default() {
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
        await Assert.That(File.Exists(Settings)).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Answering_yes_turns_steering_on_as_the_daemon_command_does() {
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

    [Test]
    public async Task Steering_is_not_asked_about_where_Claude_Code_was_not_found() {
        await Assert.That(await RunAsync([], ask: Refusing, claudeDetected: false)).IsFalse();
        await Assert.That(await RunAsync([], ask: null, claudeDetected: false)).IsFalse();
        await Assert.That(Lines).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // without a daemon the flip is refused; Windows refuses before asking
    public async Task A_daemon_that_could_not_turn_it_on_leaves_steering_off_and_says_how_to_later() {
        var on = await RunAsync([], ask: _ => true);

        await Assert.That(on).IsFalse();
        await Assert.That(Lines.Any(l => l.Contains($"daemon '{Name}' is not running", StringComparison.Ordinal))).IsTrue();
        await Assert.That(Lines.Any(l => l.Contains("kcap daemon steering on", StringComparison.Ordinal))).IsTrue();
        await Assert.That(File.Exists(Settings)).IsFalse();
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
