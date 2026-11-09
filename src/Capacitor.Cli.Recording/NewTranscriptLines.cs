namespace Capacitor.Cli.Recording;

/// <summary>What a transcript read found past a line cursor.</summary>
/// <param name="Lines">The new non-blank lines.</param>
/// <param name="LineNumbers">Their 0-based line numbers.</param>
/// <param name="NextPosition">Where the caller's line cursor goes next.</param>
/// <param name="HeldIncompleteFinalLine">A non-blank final line was held back unconsumed. A final drain flags
/// the session needs-import rather than drop a truncated tail.</param>
/// <param name="SnapshotByteLength">The byte length the read was capped at.</param>
/// <param name="SnapshotBytes">The raw bytes of this same read, when the caller asked for them, so the rewrite
/// guard hashes exactly what decoded <paramref name="Lines"/>.</param>
/// <param name="SnapshotStartOffset">The file offset <paramref name="SnapshotBytes"/> starts at, non-zero for a
/// bounded capture.</param>
public readonly record struct NewTranscriptLines(
    List<string> Lines,
    List<int>    LineNumbers,
    int          NextPosition,
    bool         HeldIncompleteFinalLine = false,
    long         SnapshotByteLength      = 0,
    byte[]?      SnapshotBytes           = null,
    long         SnapshotStartOffset     = 0);
