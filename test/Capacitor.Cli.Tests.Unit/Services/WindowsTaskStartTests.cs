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

    async Task<List<string>> StartWhile(Func<int, string> stateAtQuery, int? daemonPid = null, IReadOnlyDictionary<int, WindowsProcessEntry>? table = null) =>
        (await StartWhileWithResult(stateAtQuery, daemonPid, table)).Calls;

    /// <summary>Runs Start on a pool thread and advances the clock until it returns, recording each schtasks verb.</summary>
    async Task<(List<string> Calls, bool Started, string? Error)> StartWhileWithResult(
            Func<int, string> stateAtQuery, int? daemonPid = null, IReadOnlyDictionary<int, WindowsProcessEntry>? table = null) {
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

        string? error = null;
        var start = Task.Run(() => manager.Start("test", out error));
        while (!start.IsCompleted) {
            clock.Advance(TimeSpan.FromMilliseconds(250));
            await Task.Delay(1);
        }
        var started = await start;

        lock (calls) return ([.. calls], started, error);
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

    /// <summary>The wait outlasts the wrapper's relaunch pause, so a wrapper still without a daemon at its end is stuck;
    /// a run then would be dropped, so the start says so instead of reporting success.</summary>
    [Test]
    public async Task A_wrapper_that_never_winds_down_fails_the_start_rather_than_dropping_it() {
        var (calls, started, error) = await StartWhileWithResult(_ => Running);

        await Assert.That(started).IsFalse();
        await Assert.That(error!).Contains("still finishing its last run");
        await Assert.That(calls).DoesNotContain("/Run");
    }

    [Test]
    public async Task The_wait_outlasts_the_wrappers_relaunch_pause() =>
        await Assert.That(WindowsScheduledTaskServiceManager.WindDownWait).IsGreaterThan(TimeSpan.FromSeconds(WindowsTaskUnit.RelaunchPauseSeconds + 1));
}
