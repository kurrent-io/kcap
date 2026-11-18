namespace Capacitor.Cli.Core.Transcripts;

/// <summary>A batch could not be projected to canonical events, and its route has fallen back: the caller sends the
/// batch as raw lines.</summary>
public sealed class CanonicalEncodingException(Exception inner) : Exception("The batch could not be encoded as canonical events", inner);
