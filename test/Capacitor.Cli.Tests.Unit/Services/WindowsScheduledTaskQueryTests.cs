using Capacitor.Cli.Core.Setup;
using Capacitor.Cli.Services;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>A failed query of one task is unknown unless a listing that succeeds leaves the task out: a transaction
/// must not read Task Scheduler failing to answer as a free name.</summary>
public class WindowsScheduledTaskQueryTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    WindowsScheduledTaskServiceManager Manager(int queryExit, int listExit, string listing = "") =>
        new(Config.Root, daemonPid: _ => null, processTable: () => new Dictionary<int, WindowsProcessEntry>(),
            schtasks: args => args is ["/Query", "/FO", "LIST"] ? (listExit, listing, "") : (queryExit, "", "Access is denied."));

    [Test]
    public async Task A_query_that_fails_while_the_listing_also_fails_is_unknown() {
        await Assert.That(Manager(queryExit: 1, listExit: 1).Query("work").Probe).IsEqualTo(LabelProbe.Unknown);
    }

    [Test]
    public async Task A_query_that_fails_for_a_task_the_listing_leaves_out_is_absent() {
        await Assert.That(Manager(queryExit: 1, listExit: 0).Query("work").Probe).IsEqualTo(LabelProbe.Absent);
    }
}
