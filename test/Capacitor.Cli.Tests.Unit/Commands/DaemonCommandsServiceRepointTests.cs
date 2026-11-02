using System.Text;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// The desktop app moves the daemon tasks a Kurrent Capacitor install wrote onto its own daemon before deleting that
/// install; a task running a daemon from anywhere else is another install's and stays as it is.
[NotInParallel]
public class DaemonCommandsServiceRepointTests {
    [TempHome] public required TempHome Home { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly string Legacy = Path.GetFullPath("/local/KurrentCapacitor");
    static readonly string Own    = Path.GetFullPath("/local/kcap/current/bin/kcap-daemon.exe");
    static readonly string Npm    = Path.GetFullPath("/npm/node_modules/@kurrent/kcap/bin/kcap-daemon.exe");

    DaemonServiceCommands Commands(IServiceManager manager) =>
        new(Daemons.Store, Config.Root, Resolutions.None(Config.Root), manager, "unused", Home, TimeProvider.System) {
            HostsCaller = _ => false,
        };

    string Wrapper(string id) => WindowsTaskUnit.WrapperPath(Config.Root, id);

    void Install(string id, string binary) =>
        File.WriteAllText(Wrapper(id), WindowsTaskUnit.Wrapper(new ServiceSpec(id, binary, Config.Root.Path($"daemon-{id}.log"),
            new Dictionary<string, string>(), [])));

    WindowsScheduledTaskServiceManager Tasks(params string[] ids) => new(
        Config.Root,
        writeUnit: (path, content, encoding) => File.WriteAllText(path, content, encoding ?? Encoding.UTF8),
        daemonPid: _ => null,
        schtasks: args => args switch {
            ["/Query", "/FO", "LIST"] => (0, string.Concat(ids.Select(id => $"TaskName: \\{WindowsTaskUnit.TaskName(id)}\n")), ""),
            ["/Query", "/TN", var task, "/XML"] => (0, WindowsTaskUnit.TaskXml(
                new ServiceSpec("x", "d", "l", new Dictionary<string, string>(), []), Wrapper(WindowsTaskUnit.IdFromTaskName(task)!), @"PC\u"), ""),
            ["/Query", ..] => (0, "Status: Ready", ""),
            _ => (0, "", ""),
        });

    [Test]
    public async Task Repoints_only_the_tasks_whose_daemon_is_inside_the_folder() {
        Install("laptop", Path.Combine(Legacy, "current", "bin", "kcap-daemon.exe"));
        Install("npm", Npm);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Tasks("laptop", "npm")).RepointFrom(Legacy, Own);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(Wrapper("laptop")))).IsEqualTo(Own);
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(Wrapper("npm")))).IsEqualTo(Npm);
        await Assert.That(console.GetCapturedOutput()).Contains("Daemon 'laptop'");
    }

    [Test]
    public async Task A_folder_holding_this_install_s_own_daemon_is_refused() {
        var legacyDaemon = Path.Combine(Legacy, "current", "bin", "kcap-daemon.exe");
        Install("laptop", legacyDaemon);
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(Tasks("laptop")).RepointFrom(Path.GetDirectoryName(Own)!, Own);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(Wrapper("laptop")))).IsEqualTo(legacyDaemon);
    }

    [Test]
    public async Task A_service_kcap_cannot_repoint_fails_the_run() {
        var manager = new RecordingServiceManager(new() { ["laptop"] = Path.Combine(Legacy, "current", "bin", "kcap-daemon.exe") });
        using var console = ConsoleOutput.StartFullCapture();

        var exit = await Commands(manager).RepointFrom(Legacy, Own);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(console.GetCapturedError()).Contains("only a Scheduled Task can be repointed");
    }
}
