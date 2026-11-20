namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// A tool-result step projected before any call was waiting for it. It is emitted, with the line and time it
/// was written, by the planner step that turns out to own it.
/// <param name="Step">Its <c>step_index</c>, <see cref="long.MaxValue"/> when absent: only a planner at a lower
/// step can own it, since a result follows its call.</param>
sealed record AntigravityHeldResult(
        Guid           EventId,
        int            LineNumber,
        DateTimeOffset At,
        string?        RecordTimestamp,
        string?        Content,
        bool           IsError,
        long           Step
    );
