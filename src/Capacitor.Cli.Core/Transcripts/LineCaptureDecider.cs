namespace Capacitor.Cli.Core.Transcripts;

/// <summary>Decides a line <see cref="CaptureRule"/> leaves undecided, by running the redaction of the producer that
/// sends the lines after it: kept or lost, or still undecided when that redaction runs out of time or
/// <paramref name="ct"/> ends the replay first.</summary>
public delegate LineCapture LineCaptureDecider(string rawLine, CancellationToken ct);
