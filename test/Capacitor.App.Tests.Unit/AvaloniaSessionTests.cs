namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class AvaloniaSessionTests {
    [Test]
    public async Task Dispatch_waits_for_an_async_body_to_finish() {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task dispatch = AvaloniaSession.DispatchAsync(async () => {
            started.SetResult();
            await release.Task.ConfigureAwait(false);
            finished.SetResult();
        });
        try {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Task.WhenAny(dispatch, Task.Delay(TimeSpan.FromMilliseconds(100)));
            await Assert.That(dispatch.IsCompleted).IsFalse();
        } finally {
            release.TrySetResult();
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await dispatch;
        }
    }

    [Test]
    public async Task Dispatch_propagates_an_exception_after_an_await() {
        var failure = new InvalidOperationException("async body failed");
        Exception? caught = null;
        try {
            await AvaloniaSession.DispatchAsync(async () => {
                await Task.Yield();
                throw failure;
            });
        } catch (Exception ex) {
            caught = ex;
        }
        await Assert.That(ReferenceEquals(caught, failure)).IsTrue();
    }
}
