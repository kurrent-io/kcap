using System.Text;
using Capacitor.Cli.Services;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>Another install taking over a daemon's Scheduled Task. cmd reads a running batch file by offset, so a
/// running task is ended before its wrapper names the new binary.</summary>
public class WindowsScheduledTaskRepointTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Legacy = @"C:\Users\u\AppData\Local\KurrentCapacitor\current\bin\kcap-daemon.exe";
    const string Moved  = @"C:\Users\u\AppData\Local\kcap\current\bin\kcap-daemon.exe";

    readonly List<string> _log = [];

    string Wrapper => WindowsTaskUnit.WrapperPath(Config.Root, "laptop");

    void Install(string binary) =>
        File.WriteAllText(Wrapper, WindowsTaskUnit.Wrapper(new ServiceSpec("laptop", binary, @"C:\logs\daemon.log",
            new Dictionary<string, string> { ["KCAP_PROFILE"] = "work" }, ["--max-agents", "4"])));

    WindowsScheduledTaskServiceManager Manager(bool running) => new(
        Config.Root,
        writeUnit: (path, content, encoding) => {
            _log.Add("write");
            File.WriteAllText(path, content, encoding ?? Encoding.UTF8);
        },
        daemonPid: _ => null,
        schtasks: args => {
            _log.Add(args[0] == "/Query" && args.Contains("/XML") ? "/Query /XML" : args[0]);
            return args switch {
                ["/Query", .., "/XML"] => (0, WindowsTaskUnit.TaskXml(new ServiceSpec("laptop", "d", "l", new Dictionary<string, string>(), []), Wrapper, @"PC\u"), ""),
                ["/Query", ..]         => (0, running ? "Status: Running" : "Status: Ready", ""),
                _                      => (0, "", ""),
            };
        });

    [Test]
    public async Task A_running_task_is_ended_repointed_and_run_again_with_its_environment_kept() {
        Install(Legacy);

        var outcome = Manager(running: true).RepointDaemon("laptop", Moved, () => { _log.Add("stop"); return true; }, out var error);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Reloaded);
        await Assert.That(error).IsNull();
        await Assert.That(_log.SkipWhile(e => e != "stop")).IsEquivalentTo(["stop", "/End", "write", "/Run"], CollectionOrdering.Matching);
        var wrapper = File.ReadAllText(Wrapper);
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(wrapper)).IsEqualTo(Moved);
        await Assert.That(WindowsTaskUnit.EnvFromWrapper(wrapper)["KCAP_PROFILE"]).IsEqualTo("work");
        await Assert.That(wrapper).Contains(@"""--max-agents"" ""4""");
    }

    [Test]
    public async Task A_busy_daemon_keeps_the_old_binary() {
        Install(Legacy);

        var outcome = Manager(running: true).RepointDaemon("laptop", Moved, () => false, out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Deferred);
        await Assert.That(WindowsTaskUnit.BinaryFromWrapper(File.ReadAllText(Wrapper))).IsEqualTo(Legacy);
    }

    [Test]
    public async Task A_task_already_on_this_binary_is_left_alone() {
        Install(Moved);

        var outcome = Manager(running: true).RepointDaemon("laptop", Moved, () => throw new InvalidOperationException("must not stop"), out _);

        await Assert.That(outcome).IsEqualTo(UnitRefresh.Current);
        await Assert.That(_log).DoesNotContain("write");
    }
}
