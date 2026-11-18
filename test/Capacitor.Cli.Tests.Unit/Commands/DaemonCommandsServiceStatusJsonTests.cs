using System.Text.Json;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <remarks>Bare <c>[NotInParallel]</c>: the status JSON goes to the process-global <c>Console.Out</c>.</remarks>
[NotInParallel]
public class DaemonCommandsServiceStatusJsonTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    /// The desktop app leaves a loaded job to launchd at login only when the status names launchd as its supervisor.
    [Test]
    public async Task Status_json_names_launchd_as_the_supervisor() {
        Skip.When(OperatingSystem.IsWindows(), "getuid is POSIX-only");
        const string daemon = "/opt/homebrew/bin/kcap-daemon";
        Directory.CreateDirectory(LaunchdUnit.AgentsDir(Home));
        File.WriteAllText(LaunchdUnit.PlistPath(Home, "work"), LaunchdUnit.Plist(new ServiceSpec(
            "work", daemon, "/home/u/.config/kcap/daemon-work.log", new Dictionary<string, string>(), [])));
        var launchctl = new FakeLaunchctl(Home);
        launchctl.Load("work", daemon, running: false);
        using var console = ConsoleOutput.StartCapture();

        var exit = await new DaemonServiceCommands(Daemons.Store, Config.Root, Resolutions.None(Config.Root),
            launchctl.Manager(Daemons.Store), "work", Home, TimeProvider.System).StatusJson();

        await Assert.That(exit).IsEqualTo(0);
        using var doc = JsonDocument.Parse(console.GetCapturedOutput());
        await Assert.That(doc.RootElement.GetProperty("state").GetString()).IsEqualTo("installed");
        await Assert.That(doc.RootElement.TryGetProperty("supervisor", out var supervisor) ? supervisor.GetString() : null)
            .IsEqualTo("launchd");
    }
}
