namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>
/// Confirms that a round delivered to a PTY Codex reviewer started a turn. The submit Enter can land as
/// a newline and leave the round in the composer: under ConPTY Codex sees no bracketed paste, so a long
/// paste reads as fast typing and Enter inserts newlines until that burst drains. One more Enter goes
/// when no turn shows; a round that still starts none is reported, so the flow fails it at once instead
/// of at its inactivity bound.
/// </summary>
internal static class CodexRoundConfirmation {
    internal enum Verdict { Started, StartedAfterResubmit, NotStarted, Unavailable, Superseded, Cancelled }

    /// <param name="ResubmitWritten">Whether the extra Enter reached the terminal by the verdict; false
    /// when a dialog, a stop or a busy input lane held it back, or its write had not returned.</param>
    internal readonly record struct Result(Verdict Verdict, bool ResubmitWritten);

    /// <summary>How long each attempt waits for a turn. A submitted prompt shows within seconds, and two
    /// of these still end far inside a flow's inactivity bound.</summary>
    internal static readonly TimeSpan Window = TimeSpan.FromSeconds(30);

    /// <param name="baseline">The rollout length sampled before the round was delivered; growth from
    /// either Enter counts against it.</param>
    /// <param name="turnAttested">A turn the hooks relayed, which a submitted prompt starts.</param>
    /// <param name="resubmit">Writes one more Enter; true when it reached the terminal.</param>
    public static async Task<Result> ConfirmAsync(
            Func<long> currentLength, long baseline, Func<bool> turnAttested, Func<Task<bool>> resubmit,
            TimeSpan window, TimeSpan pollInterval, TimeProvider time, CancellationToken ct, Func<bool>? isCurrent = null) {
        var first = await CodexTurnObserver.ObserveGrowthAsync(
            currentLength, baseline, window, pollInterval, time, ct, isCurrent, turnAttested);
        if (first != CodexTurnObserver.Outcome.NotObserved) return new(Map(first, resubmitted: false), false);

        if (isCurrent is not null && !isCurrent()) return new(Verdict.Superseded, false);
        if (ct.IsCancellationRequested) return new(Verdict.Cancelled, false);

        // Not awaited before the second window: a terminal that stopped draining input holds the Enter
        // in a write nothing can cancel, and the round must still reach a verdict. The verdict is final;
        // an Enter whose write returns after it counts as not written.
        var resubmitting = resubmit();

        var second = await CodexTurnObserver.ObserveGrowthAsync(
            currentLength, baseline, window, pollInterval, time, ct, isCurrent, turnAttested);

        var written = resubmitting is { IsCompletedSuccessfully: true, Result: true };
        if (!resubmitting.IsCompletedSuccessfully)
            _ = resubmitting.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        return new(Map(second, resubmitted: true), written);
    }

    static Verdict Map(CodexTurnObserver.Outcome outcome, bool resubmitted) => outcome switch {
        CodexTurnObserver.Outcome.TurnObserved => resubmitted ? Verdict.StartedAfterResubmit : Verdict.Started,
        CodexTurnObserver.Outcome.NotObserved  => Verdict.NotStarted,
        CodexTurnObserver.Outcome.Unavailable  => Verdict.Unavailable,
        CodexTurnObserver.Outcome.Superseded   => Verdict.Superseded,
        _                                      => Verdict.Cancelled,
    };
}
