namespace Capacitor.Cli.Recording.Capture;

/// <summary>
/// Encodes the lines of one inline drain. A held line stops the drain and, with the watcher gone,
/// nothing comes back for it, so the first line to run out of time can have a second try. Only one
/// per drain, so that a machine stalling on every line does not spend the hook's whole cap here.
/// </summary>
/// <param name="patterns">Replaces both tries' patterns, for a test that must not trip a real regex deadline.</param>
public sealed class InlineDrainRedaction(TimeProvider time, SecretPatterns? patterns = null) {
    public bool CanRetry { get; private set; } = true;

    public CapturedLines Encode(string rawLine, Action<RedactionLossReason, int> reportLoss) =>
        TranscriptCapture.EncodeUntilHeld([rawLine], static () => false, time, reportLoss, patterns);

    /// <summary>The first try's redaction of a line on its own, which a replay before the drain decides a large line
    /// with.</summary>
    public RedactionOutcome Redact(string rawLine) =>
        SecretRedactor.RedactLineWithOutcome(rawLine, new RedactionBudget(time), patterns ?? SecretRedactor.WatcherPatterns);

    public CapturedLines Retry(string rawLine, Action<RedactionLossReason, int> reportLoss) {
        CanRetry = false;

        return TranscriptCapture.EncodeUntilHeld([rawLine], static () => false, time, reportLoss, patterns ?? SecretRedactor.InlineRetryPatterns);
    }
}
