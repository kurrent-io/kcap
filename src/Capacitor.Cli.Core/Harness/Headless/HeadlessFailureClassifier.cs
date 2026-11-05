using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Harness.Headless;

/// <summary>
/// Maps a provider error from a headless runner onto <see cref="ClaudeCliFailure"/>. A status wins over
/// the message when both point somewhere.
/// </summary>
internal static class HeadlessFailureClassifier {
    static readonly string[] SpendMarkers = ["credit balance", "quota", "usage limit", "rate limit"];
    static readonly string[] CredentialMarkers = ["no models available", "no api key"];

    public static ClaudeCliFailure Classify(int? statusCode, string? message) {
        if (statusCode is 401 or 403) return ClaudeCliFailure.NotSignedIn;
        if (statusCode == 429) return ClaudeCliFailure.SpendBudget;
        if (message is null) return ClaudeCliFailure.ProcessFailure;
        if (ContainsAny(message, SpendMarkers)) return ClaudeCliFailure.SpendBudget;
        return ContainsAny(message, CredentialMarkers) ? ClaudeCliFailure.NotSignedIn : ClaudeCliFailure.ProcessFailure;
    }

    /// The leading HTTP status of a message shaped like <c>401 {...}</c>, else null.
    public static int? StatusFromMessage(string? message) {
        if (message is null || message.Length < 3) return null;
        if (message.Length > 3 && message[3] != ' ') return null;
        return int.TryParse(message.AsSpan(0, 3), System.Globalization.NumberStyles.None, null, out var status) && status is >= 100 and <= 599
            ? status
            : null;
    }

    static bool ContainsAny(string message, string[] markers) =>
        markers.Any(m => message.Contains(m, StringComparison.OrdinalIgnoreCase));
}
