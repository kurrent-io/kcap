using Capacitor.Cli.Core;
using Capacitor.Cli.Harness.Copilot;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

public class CopilotEndHandoffTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WatcherPaths Paths() {
        var paths = WatcherPaths.FromEnvironment(Config.Root);
        Directory.CreateDirectory(paths.Directory);
        return paths;
    }

    [Test]
    public async Task The_handoff_covers_the_watcher_the_hook_saw_and_no_later_one() {
        var paths = Paths();
        File.WriteAllText(paths.PidFile("s"), "101\ntoken-a");

        await Assert.That(CopilotEndHandoff.Record(paths, "s")).IsTrue();
        await Assert.That(CopilotEndHandoff.Covers(paths, "s", 101, "token-a")).IsTrue();
        await Assert.That(CopilotEndHandoff.CoversCurrentWatcher(paths, "s")).IsTrue();

        File.WriteAllText(paths.PidFile("s"), "202\ntoken-b");

        await Assert.That(CopilotEndHandoff.Covers(paths, "s", 202, "token-b")).IsFalse();
        await Assert.That(CopilotEndHandoff.Covers(paths, "s", 101, "token-recycled")).IsFalse();
        await Assert.That(CopilotEndHandoff.CoversCurrentWatcher(paths, "s")).IsFalse();
    }

    [Test]
    public async Task No_watcher_means_nothing_handed_off() {
        var paths = Paths();

        await Assert.That(CopilotEndHandoff.Record(paths, "s")).IsFalse();
        await Assert.That(File.Exists(CopilotEndHandoff.MarkerFile(paths, "s"))).IsFalse();
    }

    [Test]
    public async Task Only_the_covered_watcher_retires_the_marker() {
        var paths = Paths();
        File.WriteAllText(paths.PidFile("s"), "101\ntoken-a");
        CopilotEndHandoff.Record(paths, "s");

        CopilotEndHandoff.Retire(paths, "s", 202);
        await Assert.That(File.Exists(CopilotEndHandoff.MarkerFile(paths, "s"))).IsTrue();

        CopilotEndHandoff.Retire(paths, "s", 101);
        await Assert.That(File.Exists(CopilotEndHandoff.MarkerFile(paths, "s"))).IsFalse();
    }
}
