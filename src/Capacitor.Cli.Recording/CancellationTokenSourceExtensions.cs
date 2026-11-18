namespace Capacitor.Cli.Recording;

static class CancellationTokenSourceExtensions {
    /// <summary>Cancels with its callbacks off this thread, for a caller other work waits behind. A source already
    /// disposed faults the returned task rather than throwing, and so does a callback that throws. Nobody here can act
    /// on either, and a fault left unobserved reaches the process's death rattle.</summary>
    internal static void CancelOffThread(this CancellationTokenSource source) =>
        _ = source.CancelAsync().ContinueWith(
            static task => _ = task.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
}
