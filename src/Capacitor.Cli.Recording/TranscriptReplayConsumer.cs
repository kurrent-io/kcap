namespace Capacitor.Cli.Recording;

/// <param name="WindowOnly">Only the last <see cref="TranscriptReplay.WindowLines"/> lines before the cursor.</param>
/// <param name="OnLine">Takes one line; true once it wants no more.</param>
public sealed record TranscriptReplayConsumer(bool WindowOnly, Func<string, bool> OnLine);
