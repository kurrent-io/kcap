using System.Text.Json.Nodes;
using Capacitor.Cli.Ide;

namespace Capacitor.Cli.Tests.Unit.Ide;

public class IdeOutboxTests {
    [Test]
    public async Task Messages_are_sent_one_at_a_time_in_queue_order() {
        List<string> sent = [];
        var overlapped = false;
        var active     = 0;
        using var outbox = new IdeOutbox(async (method, _) => {
            if (Interlocked.Increment(ref active) > 1) overlapped = true;
            await Task.Yield();
            sent.Add(method);
            Interlocked.Decrement(ref active);
        });

        foreach (var method in new[] { "a", "b", "c", "d" }) outbox.Send(method, new JsonObject());
        outbox.Send("dropped", new JsonObject(), () => false);
        await outbox.DrainAsync().WithinDeadlineAsync("the queue to drain");

        await Assert.That(string.Join(",", sent)).IsEqualTo("a,b,c,d");
        await Assert.That(overlapped).IsFalse();
    }

    [Test]
    public async Task Nothing_queued_is_sent_after_dispose() {
        List<string> sent = [];
        var inFlight = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release  = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outbox   = new IdeOutbox(async (method, _) => {
            sent.Add(method);
            inFlight.TrySetResult();
            await release.Task;
        });

        outbox.Send("in-flight", new JsonObject());
        outbox.Send("queued", new JsonObject());
        var drained = outbox.DrainAsync();
        await inFlight.Task.WithinDeadlineAsync("the first message to be in flight");
        outbox.Dispose();
        outbox.Send("late", new JsonObject());
        release.SetResult();
        await drained.WithinDeadlineAsync("the queue to drain");

        await Assert.That(string.Join(",", sent)).IsEqualTo("in-flight");
        await Assert.That(outbox.DrainAsync().IsCompleted).IsTrue();
    }
}
