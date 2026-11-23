using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Core.Tests.Unit;

public class BoundedTaskTests {
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(1);

    [Test]
    public async Task A_task_still_running_at_the_limit_has_not_finished() {
        var time     = new FakeTimeProvider();
        var work     = new TaskCompletionSource<int>();
        var finished = work.Task.FinishedWithinAsync(Limit, time);

        time.Advance(Limit);
        var result = await finished;

        await Assert.That(result).IsFalse();
    }

    /// <summary>Typed tasks on purpose: <c>Task&lt;T&gt;.ConfigureAwait</c> refuses to suppress throwing.</summary>
    [Test]
    [Arguments(TaskStatus.RanToCompletion)]
    [Arguments(TaskStatus.Faulted)]
    [Arguments(TaskStatus.Canceled)]
    public async Task A_task_ending_inside_the_limit_has_finished_without_throwing(TaskStatus ending) {
        var work     = new TaskCompletionSource<int>();
        var finished = work.Task.FinishedWithinAsync(Limit, new FakeTimeProvider());

        switch (ending) {
            case TaskStatus.RanToCompletion: work.SetResult(1); break;
            case TaskStatus.Faulted:         work.SetException(new InvalidOperationException("the work failed")); break;
            default:                         work.SetCanceled(); break;
        }

        var result = await finished;

        await Assert.That(result).IsTrue();
    }
}
