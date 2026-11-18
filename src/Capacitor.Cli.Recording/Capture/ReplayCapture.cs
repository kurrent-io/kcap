using Capacitor.Cli.Core.Transcripts;

namespace Capacitor.Cli.Recording.Capture;

/// <summary>Decides, for a replay, how a producer captured a line the capture rule leaves to the redaction: by running
/// that producer's redaction on it. A line it sends is kept and one it replaces with a capture-loss line is lost. A
/// redaction out of its own time, or the replay's, leaves the line undecided and the replay stops there: a replay never
/// holds, retries or marks a line.</summary>
public static class ReplayCapture {
    /// <param name="redact">The producer's redaction of one line, with its own patterns and budget.</param>
    public static LineCaptureDecider With(Func<string, RedactionOutcome> redact) =>
        (line, ct) => {
            if (!ct.CanBeCanceled) return Of(redact(line));
            if (ct.IsCancellationRequested) return LineCapture.Undecided;

            // Off this thread, so the replay's time ends the wait even while a long line is still being redacted.
            var redacting = Task.Run(() => redact(line), CancellationToken.None);

            try {
                redacting.Wait(ct);
            } catch (OperationCanceledException) {
                // It ends within its own budget; nobody waits for it.
                _ = redacting.ContinueWith(static task => _ = task.Exception, CancellationToken.None,
                    TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

                return LineCapture.Undecided;
            }

            return Of(redacting.Result);
        };

    static LineCapture Of(RedactionOutcome outcome) =>
        outcome.Loss switch {
            null                                 => LineCapture.Kept,
            { } reason when reason.IsTransient() => LineCapture.Undecided,
            _                                    => LineCapture.Lost,
        };
}
