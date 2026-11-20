namespace Capacitor.Cli.Core.Transcripts;

/// <summary>The hub's answer to a canonical batch. A batch the server cannot take is <paramref name="Unsupported"/>: it
/// stored nothing of it, and the lines go raw.</summary>
public readonly record struct CanonicalSendResult(int NextLineNumber, bool Unsupported);
