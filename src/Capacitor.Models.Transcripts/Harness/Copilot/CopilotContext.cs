namespace Capacitor.Models.Transcripts.Harness.Copilot;

/// Per-stream state for a Copilot transcript. All of it spans the session, so nothing is cleared at a batch
/// boundary: an attachment's bytes and the prompt that names them, or a fork's copied lines and its marker,
/// routinely arrive in different batches.
public sealed class CopilotContext : TranscriptContext {
    internal CopilotContext(string sessionId) => SessionId = sessionId;

    /// Canonical form; a fork marker links the stream to a parent only when it names this session as the fork.
    internal string SessionId { get; }

    /// What a caller rebuilding a context knows from what it stored. <paramref name="isFork"/>: it stored a
    /// SessionStarted carrying <see cref="CopilotTranscriptEvents.ForkMarkerKey"/>. <paramref name="transcriptStart"/>:
    /// the payload time of the first SessionStarted it stored without that key, or null when it stored none, in which
    /// case every line is held back until the transcript's own <c>session.start</c> arrives, since nothing else tells a
    /// fork's copied lines from its own. The parent's spend is not recoverable, so a rebuilt fork reports no billing
    /// totals. A context never seeded holds nothing back.
    /// <paramref name="movedFromLocal"/>: the transcript is a chat moved from Local. <paramref name="moveMarked"/>: it
    /// stored the <see cref="CopilotTranscriptEvents.MoveMarkerKey"/> link, which says the copied history is over, with
    /// the line of the resume that ended it when known. Without the link, the lines up to the next resume or marker are
    /// held back as the history's.
    public void Seed(bool isFork, DateTimeOffset? transcriptStart, bool movedFromLocal = false, bool moveMarked = false, int? movedHistoryEndsAtLine = null) {
        if (isFork) IsFork = true;

        if (movedFromLocal) {
            MovedFromLocal           = true;
            InMovedHistory           = !moveMarked;
            MovedHistoryEndsAtLine ??= moveMarked ? movedHistoryEndsAtLine : null;
        }

        if (transcriptStart is null) StartUnknown = true;
        else CopiedLinesBefore ??= transcriptStart;
    }

    /// The envelope id of the latest <c>session.start</c> or <c>session.resume</c>; billing totals group by it.
    internal Guid? RunMarker { get; set; }

    internal bool RunHasUsageRecords { get; set; }

    /// Null until the run's first assistant message. Only a seen false lets a shutdown count output, so a lost
    /// context can under-count output but never double it.
    internal bool? RunMessagesReportOutput { get; set; }

    /// A fork copies its parent's lines, with their own times, right after its <c>session.start</c>: a line timed
    /// before that start is a copy.
    internal DateTimeOffset? CopiedLinesBefore { get; set; }

    internal bool StartUnknown { get; set; }

    internal bool MovedFromLocal { get; set; }

    /// Inside the Local chat's history that a moved chat repeats, from its start to its first resume.
    internal bool InMovedHistory { get; set; }

    /// The line of the resume that ended a moved chat's history, so a line before it sent again is known for a copy.
    internal int? MovedHistoryEndsAtLine { get; set; }

    /// A fork's cumulative totals include its parent's.
    internal bool IsFork { get; set; }

    /// The parent's totals at the end of the copied lines; null when not known. Kept in memory only: a viewer can
    /// hold the fork without the parent, so nothing the fork stores may carry them.
    internal long? InheritedNanoAiu { get; set; }

    internal double? InheritedPremiumRequests { get; set; }

    internal CopilotAssetStash Assets { get; } = new();
}
