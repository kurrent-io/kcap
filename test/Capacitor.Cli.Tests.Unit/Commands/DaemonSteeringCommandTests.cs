using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
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
    [TempDir]         public required TempDir         Tmp     { get; init; }

    const string Name = "t";

    static readonly SteeringSetAckDto TurnedOn  = new(true, null, SteeringIpc.On);
    static readonly SteeringSetAckDto TurnedOff = new(true, null, SteeringIpc.Off);

    StringWriter Out { get; } = new();
    StringWriter Err { get; } = new();

    StandardStreams Streams => new(Out, Err, TextReader.Null);

    ProfileContext Profiles => Resolutions.None(Config.Root);

    string Settings => new ClaudePaths(Home, null).UserSettings;

    string PluginDir => Tmp.PathTo("kcap-plugin");

    AccountStore Accounts => new(Tmp.PathTo("accounts"));

    ClaudeSteeringMod Mod => ClaudeSteeringMod.For(TestHarnesses.Under(Home), Home, Accounts);

    string Socket => Daemons.Store.SocketPath(Name);

    /// <summary>A profile whose daemon is <paramref name="daemon"/>, the one the steering mod finds.</summary>
    DaemonCommands Daemon(ConfigRoot? config = null, string daemon = Name) {
        var root = config ?? Config.Root;

        return new(Daemons.Store, root, Resolutions.Of(new Profile { Daemon = new DaemonSettings { Name = daemon } }), Home,
                TestHarnesses.Under(Home), TestBinaries.None, TimeProvider.System, Streams) {
            Steering = Mod
        };
    }

    Task<int> SteeringAsync(string state, string name = Name, ConfigRoot? config = null) =>
        Daemon(config, name).HandleAsync(["daemon", "steering", state, "--name", name]);

    void InstallKcap(string settings) => ClaudePluginWriter.Install(settings, null, PluginDir);

    static JsonNode? Enabled(string settings) =>
        JsonNode.Parse(File.ReadAllText(settings))!["enabledPlugins"]?[ClaudePluginInstaller.SteeringPluginKey];

    JsonNode? Enabled() => Enabled(Settings);

    void EditSettings(Action<JsonObject> edit) {
        var root = JsonNode.Parse(File.ReadAllText(Settings))!.AsObject();
        edit(root);
        File.WriteAllText(Settings, root.ToJsonString());
    }

    void ClearOutput() {
        Out.GetStringBuilder().Clear();
        Err.GetStringBuilder().Clear();
    }

    /// <summary>With kcap's plugin installed, a refusal that wrote the steering plugin anyway would show in the file.</summary>
    [Test]
    public async Task Steering_is_refused_on_windows() {
        InstallKcap(Settings);
        var before = File.ReadAllText(Settings);

        var exit = await DaemonSteeringCommand.SetAsync(Daemons.Store, Profiles, TimeProvider.System, Streams, ["on", "--name", Name], Mod, offered: false);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains("isn't offered on Windows");
        await Assert.That(Err.ToString()).DoesNotContain("not running");
        await Assert.That(Out.ToString()).IsEmpty();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    /// <summary>The switch flips first, so a daemon that never flipped it leaves Claude's settings as they were.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // refused there before any daemon is asked
    public async Task Steering_on_says_when_no_daemon_is_running() {
        InstallKcap(Settings);
        var before = File.ReadAllText(Settings);

        var exit = await SteeringAsync(SteeringIpc.On);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains($"daemon '{Name}' is not running");
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_on_says_what_the_daemon_set() {
        InstallKcap(Settings);
        ClaudePluginWriter.InstallSteering(Settings);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        var exit = await SteeringAsync(SteeringIpc.On);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
    }

    /// <summary>Once per settings file: a second <c>on</c> leaves the entry as the person left it, whether they disabled
    /// the plugin in Claude Code or uninstalled it, and says what that means.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Turning_steering_on_installs_and_enables_the_plugin_once() {
        InstallKcap(Settings);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        await Assert.That(await SteeringAsync(SteeringIpc.On)).IsEqualTo(0);
        await Assert.That(Out.ToString()).Contains($"Installed the kcap-steering plugin for Claude Code in {Settings}");
        await Assert.That(Enabled()!.GetValue<bool>()).IsTrue();
        await Assert.That(JsonNode.Parse(File.ReadAllText(Settings))!["extraKnownMarketplaces"]!["kcap"]!["source"]!["path"]!.GetValue<string>())
            .IsEqualTo(PluginDir);

        EditSettings(root => root["enabledPlugins"]![ClaudePluginInstaller.SteeringPluginKey] = false);
        ClearOutput();

        await Assert.That(await SteeringAsync(SteeringIpc.On)).IsEqualTo(0);
        await Assert.That(Enabled()!.GetValue<bool>()).IsFalse();
        await Assert.That(Out.ToString()).DoesNotContain("Installed");
        await Assert.That(Out.ToString()).Contains($"kcap-steering isn't enabled in {Settings}");

        EditSettings(root => root["enabledPlugins"]!.AsObject().Remove(ClaudePluginInstaller.SteeringPluginKey));
        ClearOutput();

        await Assert.That(await SteeringAsync(SteeringIpc.On)).IsEqualTo(0);
        await Assert.That(Enabled()).IsNull();
        await Assert.That(Out.ToString()).Contains($"kcap-steering isn't enabled in {Settings}");
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On, SteeringIpc.On, SteeringIpc.On]);
        await Assert.That(Err.ToString()).IsEmpty();
    }

    /// <summary>The mod looks its daemon up with no name, so it would never reach another: nothing is asked of that
    /// daemon, and no plugin is installed for it.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_on_refuses_a_daemon_other_than_the_profiles() {
        InstallKcap(Settings);
        await using var other = FakeSteeringDaemon.Answering(Daemons.Store.SocketPath("other"), TurnedOn);

        var exit = await Daemon().HandleAsync(["daemon", "steering", "on", "--name", "other"]);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(Err.ToString()).Contains($"finds this profile's daemon, '{Name}', so it would never reach daemon 'other'");
        await Assert.That(other.Asked).IsEmpty();
        await Assert.That(Enabled()).IsNull();
    }

    /// <summary>Switching steering off reaches any daemon: a stray one left on is worth turning off.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_off_reaches_a_daemon_other_than_the_profiles() {
        await using var other = FakeSteeringDaemon.Answering(Daemons.Store.SocketPath("other"), TurnedOff);

        await Assert.That(await Daemon().HandleAsync(["daemon", "steering", "off", "--name", "other"])).IsEqualTo(0);
        await Assert.That(other.Asked).IsEquivalentTo([SteeringIpc.Off]);
    }

    /// <summary>Profiles share the machine's Claude settings, so a second profile's daemon finds the plugin already
    /// handled: the marker belongs to the settings file, not to either profile's config.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task A_second_profile_finds_the_plugin_as_the_first_left_it() {
        InstallKcap(Settings);
        await using (FakeSteeringDaemon.Answering(Socket, TurnedOn)) await SteeringAsync(SteeringIpc.On);
        EditSettings(root => root["enabledPlugins"]![ClaudePluginInstaller.SteeringPluginKey] = false);
        ClearOutput();

        using var second = new TempConfigRoot("second-profile");
        await using var daemon = FakeSteeringDaemon.Answering(Daemons.Store.SocketPath("u"), TurnedOn);

        await Assert.That(await SteeringAsync(SteeringIpc.On, "u", second.Root)).IsEqualTo(0);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
        await Assert.That(Enabled()!.GetValue<bool>()).IsFalse();
        await Assert.That(Out.ToString()).DoesNotContain("Installed");
    }

    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Turning_steering_off_leaves_the_plugin_installed() {
        InstallKcap(Settings);
        await using (FakeSteeringDaemon.Answering(Socket, TurnedOn)) await SteeringAsync(SteeringIpc.On);
        var installed = File.ReadAllText(Settings);
        ClearOutput();

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
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon, and the read-only case needs Unix modes
    [Arguments("unreadable", "as settings, so it left the file alone")]
    [Arguments("read-only", "couldn't be written")]
    [UnsupportedOSPlatform("windows")]
    public async Task A_failed_plugin_install_still_turns_steering_on_and_says_how_to_install_it(string failure, string why) {
        var claudeHome = Path.GetDirectoryName(Settings)!;
        if (failure == "unreadable") Home.CreateFile([".claude", "settings.json"], "{ broken");
        else InstallKcap(Settings);
        var before = File.ReadAllText(Settings);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        if (failure == "read-only") File.SetUnixFileMode(claudeHome, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        int exit;
        try {
            exit = await SteeringAsync(SteeringIpc.On);
        } finally {
            File.SetUnixFileMode(claudeHome, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(Err.ToString()).Contains($"the kcap-steering plugin isn't installed in {Settings}");
        await Assert.That(Err.ToString()).Contains(why);
        await Assert.That(Err.ToString()).Contains("claude plugin install kcap-steering@kcap");
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(Settings)).IsFalse();
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(before);
    }

    /// <summary>Only kcap's own plugin install registers its marketplace, so steering adds nothing where it hasn't run,
    /// and says what to run first rather than a by-hand add.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task Steering_on_where_kcaps_plugin_is_not_installed_changes_nothing_and_says_why() {
        const string mine = """{ "model": "opus" }""";
        Home.CreateFile([".claude", "settings.json"], mine);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        var exit = await SteeringAsync(SteeringIpc.On);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(daemon.Asked).IsEquivalentTo([SteeringIpc.On]);
        await Assert.That(Out.ToString()).IsEqualTo($"Steering is on for daemon '{Name}'.{Out.NewLine}");
        await Assert.That(Err.ToString()).Contains($"needs kcap's Claude Code plugin, which isn't installed in {Settings}");
        await Assert.That(Err.ToString()).Contains("`kcap plugin install` adds it, and then `kcap daemon steering on` installs the steering plugin");
        await Assert.That(Err.ToString()).DoesNotContain("marketplace");
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(mine);
        await Assert.That(ClaudePluginInstaller.HasInstalledSteering(Settings)).IsFalse();
    }

    /// <summary>The upgrade-time refresh reads a <c>kcap</c> marketplace entry as kcap installed, so <c>steering on</c>
    /// must leave nothing it could read that way.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task An_if_installed_refresh_after_steering_on_still_does_not_install_kcap() {
        const string mine = """{ "model": "opus" }""";
        Home.CreateFile([".claude", "settings.json"], mine);
        await using (FakeSteeringDaemon.Answering(Socket, TurnedOn)) await SteeringAsync(SteeringIpc.On);

        var env = TestPluginEnvironment.For(Home, Tmp.CreateDir("plugin")) with { Stdout = new StringWriter(), Stderr = new StringWriter() };
        var exit = await new PluginCommand(env, new WorkingDirectory(Tmp.CreateDir("work"))).HandleAsync(["plugin", "install", "--if-installed"]);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(File.ReadAllText(Settings)).IsEqualTo(mine);
        await Assert.That(ClaudePluginInstaller.IsInstalled(Settings)).IsFalse();
    }

    /// <summary>The mod follows the kcap plugin into each Claude config dir <c>kcap accounts</c> records, and goes nowhere
    /// the kcap plugin isn't.</summary>
    [Test]
    [ExcludeOn(OS.Windows)] // a Unix-domain socket stands in for the daemon
    public async Task The_mod_goes_into_every_account_kcap_is_installed_in_and_nowhere_else() {
        string[] wired = [Home.PathTo(".claude-work"), Home.PathTo(".claude-play")];
        var bare = Home.PathTo(".claude-bare");
        const string bareSettings = """{ "model": "sonnet" }""";
        foreach (var dir in wired) InstallKcap(AccountLayouts.Claude(Home, dir).UserSettings);
        Home.CreateFile([".claude-bare", "settings.json"], bareSettings);
        foreach (var dir in (string[])[.. wired, bare]) AccountAdoption.EnsureDefault(Accounts, HarnessId.Claude, dir, TimeProvider.System);
        await using var daemon = FakeSteeringDaemon.Answering(Socket, TurnedOn);

        var exit = await SteeringAsync(SteeringIpc.On);

        await Assert.That(exit).IsEqualTo(0);
        foreach (var settings in wired.Select(dir => AccountLayouts.Claude(Home, dir).UserSettings)) {
            await Assert.That(Enabled(settings)!.GetValue<bool>()).IsTrue();
            await Assert.That(Out.ToString()).Contains($"Installed the kcap-steering plugin for Claude Code in {settings}");
        }
        await Assert.That(File.ReadAllText(Path.Combine(bare, "settings.json"))).IsEqualTo(bareSettings);
        await Assert.That(File.Exists(Path.Combine(bare, ClaudePluginInstaller.SteeringMarkerFileName))).IsFalse();
        await Assert.That(File.Exists(Settings)).IsFalse();
        await Assert.That(Err.ToString()).IsEmpty();
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
