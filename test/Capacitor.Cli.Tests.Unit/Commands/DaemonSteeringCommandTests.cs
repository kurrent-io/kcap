using System.Net.Sockets;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Harness.Claude;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap daemon steering on|off</c> and <c>kcap daemon steering-socket</c>, against control and steering
/// sockets standing in for a daemon's, with Claude Code's settings under a throwaway home.</summary>
public class DaemonSteeringCommandTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    const string Name = "t";

    static readonly SteeringSetAckDto TurnedOn  = new(true, null, SteeringIpc.On);
    static readonly SteeringSetAckDto TurnedOff = new(true, null, SteeringIpc.Off);

    StringWriter Out { get; } = new();
    StringWriter Err { get; } = new();

    StandardStreams Streams => new(Out, Err, TextReader.Null);

    ProfileContext Profiles => Resolutions.None(Config.Root);

    string Settings => new ClaudePaths(Home, null).UserSettings;

    string PluginDir => Home.PathTo("kcap-plugin");

    ClaudeSteeringMod Mod => new(Settings, () => PluginDir);

    string Socket => Daemons.Store.SocketPath(Name);

    DaemonCommands Daemon(ClaudeSteeringMod? mod = null) => new(
        Daemons.Store, Config.Root, Profiles, Home, TestHarnesses.Under(Home), TestBinaries.None, TimeProvider.System, Streams) {
        Steering = mod ?? Mod
    };

    Task<int> SteeringAsync(string state, ClaudeSteeringMod? mod = null) =>
        Daemon(mod).HandleAsync(["daemon", "steering", state, "--name", Name]);

    JsonNode? Enabled() => JsonNode.Parse(File.ReadAllText(Settings))!["enabledPlugins"]?[ClaudePluginInstaller.SteeringPluginKey];

    void EditSettings(Action<JsonObject> edit) {
        var root = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        edit(root);
        File.WriteAllText(Settings, root.ToJsonString());
    }

    [Test]
    public async Task Steering_is_refused_on_windows() {
        var exit = await DaemonSteeringCommand.SetAsync(Daemons.Store, Profiles, TimeProvider.System, Streams, ["on", "--name", Name], Mod, offered: false);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("isn't offered on Windows");
        await Assert.That(Err.ToString()).DoesNotContain("not running");
        await Assert.That(Out.ToString()).IsEmpty();
        await Assert.That(File.Exists(Settings)).IsFalse();
    }

    /// <summary>The switch flips first, so a daemon that never flipped it leaves Claude's settings as they were.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // refused there before any daemon is asked
    public async Task Steering_on_says_when_no_daemon_is_running() {
        var exit = await SteeringAsync(SteeringIpc.On);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains($"daemon '{Name}' is not running");
        await Assert.That(File.Exists(Settings)).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_on_says_what_the_daemon_set() {
        ClaudePluginInstaller.WriteSteeringMarker(Settings);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        var exit = await SteeringAsync(SteeringIpc.On);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
    }

    /// <summary>Once per settings file: a second <c>on</c> leaves the entry as the person left it, whether they disabled
    /// the plugin in Claude Code or uninstalled it.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Turning_steering_on_installs_and_enables_the_plugin_once() {
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        await Assert.That(await SteeringAsync(SteeringIpc.On)).IsEqualTo(0);
        await Assert.That(Out.ToString()).Contains($"Installed the kcap-steering plugin for Claude Code in {Settings}");
        await Assert.That(Enabled()!.GetValue<bool>()).IsTrue();
        await Assert.That(JsonNode.Parse(File.ReadAllText(Settings))!["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>())
            .IsEqualTo(PluginDir);

        EditSettings(root => root["enabledPlugins"]![ClaudePluginInstaller.SteeringPluginKey] = false);
        Out.GetStringBuilder().Clear();

        await Assert.That(await SteeringAsync(SteeringIpc.On)).IsEqualTo(0);
        await Assert.That(Enabled()!.GetValue<bool>()).IsFalse();
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");

        EditSettings(root => root["enabledPlugins"]!.AsObject().Remove(ClaudePluginInstaller.SteeringPluginKey));

        await Assert.That(await SteeringAsync(SteeringIpc.On)).IsEqualTo(0);
        await Assert.That(Enabled()).IsNull();
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On, SteeringIpc.On, SteeringIpc.On]);
        await Assert.That(Err.ToString()).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Turning_steering_off_leaves_the_plugin_installed() {
        await using (FakeSteeringDaemon.Answering(Socket, TurnedOn)) await SteeringAsync(SteeringIpc.On);
        var installed = File.ReadAllText(Settings);
        Out.GetStringBuilder().Clear();

        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOff);
        var exit = await SteeringAsync(SteeringIpc.Off);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.Off]);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is off for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(installed);
        await Assert.That(Enabled()!.GetValue<bool>()).IsTrue();
    }

    /// <summary>The switch is on whatever happens to the settings write, and nothing is marked done, so the next
    /// <c>on</c> tries again.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    [Arguments("malformed", "as settings, so it left the file alone")]
    [Arguments("no plugin directory", "kcap's plugin directory wasn't found")]
    public async Task A_failed_plugin_install_still_turns_steering_on_and_says_how_to_install_it(string failure, string why) {
        const string broken = "{ broken";
        if (failure == "malformed") Home.CreateFile([".claude", "settings.json"], broken);
        var mod = failure == "malformed" ? Mod : new ClaudeSteeringMod(Settings, () => null);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        var exit = await SteeringAsync(SteeringIpc.On, mod);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(Err.ToString()).Contains("the kcap-steering plugin isn't installed");
        await Assert.That(Err.ToString()).Contains(why);
        await Assert.That(Err.ToString()).Contains("claude plugin install kcap-steering@kcap");
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(Settings)).IsFalse();
        if (failure == "malformed") await Assert.That(File.ReadAllText(Settings)).IsEqualTo(broken);
        else await Assert.That(File.Exists(Settings)).IsFalse();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_off_names_a_daemon_too_old_for_the_switch() {
        await using var daemon = new FakeSteeringDaemon(Daemons.Store.SocketPath(Name), answer: null);

        var exit = await Daemon().HandleAsync(["daemon", "steering", "off", "--name", Name]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("doesn't support steering");
        await Assert.That(daemon.Asked).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_on_says_why_the_daemon_could_not_open_its_socket() {
        await using var daemon = FakeSteeringDaemon.Answering(Daemons.Store.SocketPath(Name),
            new SteeringSetAckDto(false, SteeringIpc.Unbindable, SteeringIpc.Off, "Address already in use"));

        var exit = await Daemon().HandleAsync(["daemon", "steering", "on", "--name", Name]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("could not open its steering socket, so the switch is still off: Address already in use");
    }

    /// <summary>The mod runs this to find the daemon, so a daemon that can't be steered must leave stdout empty
    /// rather than say why.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_socket_prints_nothing_when_off() {
        var path = Daemons.Store.SteeringSocketPath(Name);

        // Never bound: the switch is off, or no daemon of this name ever turned it on.
        await Assert.That(await Daemon().HandleAsync(["daemon", "steering-socket", "--name", Name])).IsEqualTo(0);

        // A socket file nothing listens on: a daemon that died with the switch on. A closed socket unlinks the file it
        // bound, so the file is moved out from under it first.
        Daemons.CreateDir(Name);
        using (var dead = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified)) {
            dead.Bind(new UnixDomainSocketEndPoint(path + ".bound"));
            File.Move(path + ".bound", path);
        }
        await Assert.That(File.Exists(path)).IsTrue();
        await Assert.That(await Daemon().HandleAsync(["daemon", "steering-socket", "--name", Name])).IsEqualTo(0);

        // Listening, on a platform where steering isn't offered.
        File.Delete(path);
        using var live = Listen(path);
        await Assert.That(await DaemonSteeringCommand.PrintSocketAsync(Daemons.Store, Profiles, TimeProvider.System, Streams, ["--name", Name], offered: false))
            .IsEqualTo(0);

        await Assert.That(Out.ToString()).IsEmpty();
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_socket_prints_the_path_alone_while_the_daemon_listens() {
        var path = Daemons.Store.SteeringSocketPath(Name);
        Daemons.CreateDir(Name);
        using var live = Listen(path);

        var exit = await Daemon().HandleAsync(["daemon", "steering-socket", "--name", Name]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString()).IsEqualTo(path + Out.NewLine);
        await Assert.That(Err.ToString()).IsEmpty();
    }

    static Socket Listen(string path) {
        var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Bind(new UnixDomainSocketEndPoint(path));
        socket.Listen(1);

        return socket;
    }
}
