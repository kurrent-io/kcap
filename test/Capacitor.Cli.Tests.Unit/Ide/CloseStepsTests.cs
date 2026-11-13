using Capacitor.Cli.Ide;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit.Ide;

public class CloseStepsTests {
    static readonly TimeSpan Budget = TimeSpan.FromSeconds(3);

    [Test]
    public async Task A_step_that_throws_does_not_skip_the_next() {
        var log = new StringWriter();
        List<string> ran = [];

        await CloseSteps.RunAsync([
            ("first", () => { ran.Add("first"); throw new InvalidOperationException("first broke"); }),
            ("second", async () => { ran.Add("second"); await Task.Yield(); throw new InvalidOperationException("second broke"); }),
            ("third", () => { ran.Add("third"); return ValueTask.CompletedTask; }),
        ], Budget, new FakeTimeProvider(), log);

        await Assert.That(ran).IsEquivalentTo(["first", "second", "third"], CollectionOrdering.Matching);
        await Assert.That(log.ToString()).Contains("first broke");
        await Assert.That(log.ToString()).Contains("second broke");
    }

    [Test]
    public async Task A_step_that_hangs_is_abandoned_at_the_budget() {
        var time  = new FakeTimeProvider();
        var log   = new StringWriter();
        var never = new TaskCompletionSource();
        var next  = false;

        var closing = CloseSteps.RunAsync([
            ("hung", () => new ValueTask(never.Task)),
            ("next", () => { next = true; return ValueTask.CompletedTask; }),
        ], Budget, time, log);
        time.Advance(Budget - TimeSpan.FromMilliseconds(1));
        var beforeTheBudget = closing.IsCompleted;
        time.Advance(TimeSpan.FromMilliseconds(1));
        await closing.WithinDeadlineAsync("the close to give up on the hung step");

        await Assert.That(beforeTheBudget).IsFalse();
        await Assert.That(next).IsTrue();
        await Assert.That(log.ToString()).IsEmpty();
    }
}
