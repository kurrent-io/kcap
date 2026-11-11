using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Capacitor.Cli.Recording.Tests.Unit;

/// <summary>A fault nobody observed is reported when its task is finalized, so each test forces a collection, with the
/// cancel in a method of its own so nothing on this stack keeps the task alive.</summary>
[NotInParallel]
public class CancellationTokenSourceExtensionsTests {
    [MethodImpl(MethodImplOptions.NoInlining)]
    static void CancelADisposedSource(Action<CancellationTokenSource> cancel) {
        var source = new CancellationTokenSource();
        source.Dispose();
        cancel(source);
    }

    static IReadOnlyList<Exception> UnobservedAfter(Action<CancellationTokenSource> cancel) {
        var unobserved = new ConcurrentQueue<Exception>();

        void Note(object? sender, UnobservedTaskExceptionEventArgs args) {
            if (args.Exception.InnerException is not ObjectDisposedException) return;

            unobserved.Enqueue(args.Exception);
            args.SetObserved();
        }

        TaskScheduler.UnobservedTaskException += Note;

        try {
            CancelADisposedSource(cancel);

            for (var i = 0; i < 3; i++) {
                GC.Collect();
                GC.WaitForPendingFinalizers();
            }
        } finally {
            TaskScheduler.UnobservedTaskException -= Note;
        }

        return [..unobserved];
    }

    /// <summary>The fault the helper exists for. Without it, the test below would pass whatever the helper did.</summary>
    [Test]
    public async Task A_plain_cancel_of_a_disposed_source_leaves_a_fault_nobody_observes() =>
        await Assert.That(UnobservedAfter(source => _ = source.CancelAsync())).IsNotEmpty();

    [Test]
    public async Task Cancelling_a_disposed_source_off_the_thread_leaves_no_fault_behind() =>
        await Assert.That(UnobservedAfter(source => source.CancelOffThread())).IsEmpty();
}
