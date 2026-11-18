namespace Capacitor.Cli.Core.Transcripts;

/// <summary>What capturing a raw transcript line makes of it, as far as the line alone says.</summary>
public enum LineCapture {
    /// The line is sent, redacted.
    Kept,

    /// A capture-loss line is sent in its place.
    Lost,

    /// Only running the redaction can say: a rewrite could take the line past the record limit.
    Undecided,
}
