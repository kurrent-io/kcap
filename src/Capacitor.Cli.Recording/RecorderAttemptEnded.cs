namespace Capacitor.Cli.Recording;

/// <summary>How one attempt at recording a source ended. A host disposes each attempt's recorder, so this is all its
/// owner gets of it. It is told before the source leaves the host, so a finish that has returned has been reported.</summary>
/// <param name="Source">The source the attempt recorded.</param>
/// <param name="SourceEnded">No attempt follows. False means the host retries.</param>
/// <param name="LostClaim">The claim went, or the host abandoned the source, and either way it is no longer the owner's to
/// let go of.</param>
/// <param name="EndedIdle">The transcript went idle and the recorder ended the source.</param>
/// <param name="SpawnedChildKeys">The child sources this attempt started. The next attempt knows only those it starts
/// itself, so nothing else will stop these.</param>
/// <param name="Failure">What failed the attempt or its final drain; null when nothing did.</param>
/// <param name="LeftLines">Its final drain left lines it neither sent nor spooled. A redaction hold ends that way with no
/// <paramref name="Failure"/>, so nothing else says those lines are still to go.</param>
public sealed record RecorderAttemptEnded(
    RecordingSource Source, bool SourceEnded, bool LostClaim, bool EndedIdle, IReadOnlyCollection<string> SpawnedChildKeys, Exception? Failure, bool LeftLines = false);
