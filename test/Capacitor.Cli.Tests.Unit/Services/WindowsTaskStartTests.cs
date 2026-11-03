using Capacitor.Cli.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.Services;

/// <summary>The task drops a /Run that arrives while an instance is live and still reports success, so a start
/// waits for a wrapper whose daemon has exited to finish before it runs the task.</summary>
public class WindowsTaskStartTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Running = "Status: Running";
    const string Ready   = "Status: Ready";

    static readonly IReadOnlyDictionary<int, WindowsProcessEntry> TaskDaemon = new Dictionary<int, WindowsProcessEntry> {
        [30] = new(20, "kcap-daemon.exe"), [20] = new(10, "cmd.exe"), [10] = new(1, "conhost.exe"),
    };

    /// <summary>Runs Start on a pool thread and advances the clock until it returns, recording each schtasks verb.</summary>
    async Task<List<string>> StartWhile(Func<int, string> stateAtQuery, int? daemonPid = null, IReadOnlyDictionary<int, WindowsProcessEntry>? table = null) {
        var clock   = new FakeTimeProvider();
        var calls   = new List<string>();
        var queries = 0;
        var manager = new WindowsScheduledTaskServiceManager(Config.Root,
            daemonPid: _ => daemonPid,
            processTable: () => table ?? new Dictionary<int, WindowsProcessEntry>(),
            schtasks: args => {
                lock (calls) {
                    calls.Add(args[0] == "/Query" ? $"query:{stateAtQuery(queries++)}" : args[0]);
                    return (0, args[0] == "/Query" ? stateAtQuery(queries - 1) : "", "");
                }
            },
            time: clock);

        var start = Task.Run(() => manager.Start("test", out _));
        while (!start.IsCompleted) {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }
        await start;

        lock (calls) return [.. calls];
    }

    [Test]
    public async Task A_start_waits_for_a_wrapper_whose_daemon_has_exited() {
        var calls = await StartWhile(n => n < 3 ? Running : Ready);

        await Assert.That(string.Join(",", calls))
            .IsEqualTo($"query:{Running},query:{Running},query:{Running},query:{Ready},/Run");
    }

    /// <summary>A task already running its daemon has nothing to wait for; the /Run it drops changes nothing.</summary>
    [Test]
    public async Task A_task_running_its_daemon_is_run_without_waiting() {
        var calls = await StartWhile(_ => Running, daemonPid: 30, table: TaskDaemon);

        await Assert.That(string.Join(",", calls)).IsEqualTo($"query:{Running},/Run");
    }

    /// <summary>A wrapper that never finishes is one restarting its daemon itself; the start runs once the wait is spent.</summary>
    [Test]
    public async Task A_wrapper_that_never_winds_down_is_run_after_the_wait() {
        var calls = await StartWhile(_ => Running);

        await Assert.That(calls[^1]).IsEqualTo("/Run");
        await Assert.That(calls.Count(c => c.StartsWith("query", StringComparison.Ordinal))).IsGreaterThan(1);
    }
}
