namespace Capacitor.Cli.Capture;

/// <summary>
/// Encodes the lines of one inline drain. A held line stops the drain and, with the watcher gone,
/// nothing comes back for it, so the first line to run out of time can have a second try. Only one
/// per drain, so that a machine stalling on every line does not spend the hook's whole cap here.
/// </summary>
/// <param name="patterns">Replaces both tries' patterns, for a test that must not trip a real regex deadline.</param>
internal sealed class InlineDrainRedaction(TimeProvider time, SecretPatterns? patterns = null) {
    // With the record budget this holds a line for about 1.4 s at most, inside the shortest cap a
    // hook puts around a drain, about 2 s.
    static readonly Lazy<SecretPatterns> RetryPatterns =
        new(() => SecretRedactor.WatcherPatterns.WithMatchTimeout(TimeSpan.FromMilliseconds(400)));

    public bool CanRetry { get; private set; } = true;

    public CapturedLines Encode(string rawLine, Action<RedactionLossReason, int> reportLoss) =>
        TranscriptCapture.EncodeUntilHeld([rawLine], static () => false, time, reportLoss, patterns);

    public CapturedLines Retry(string rawLine, Action<RedactionLossReason, int> reportLoss) {
        CanRetry = false;

        return TranscriptCapture.EncodeUntilHeld([rawLine], static () => false, time, reportLoss, patterns ?? RetryPatterns.Value);
    }
}
