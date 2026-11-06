using System.Text.RegularExpressions;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>Compiles the watcher patterns before any test runs. A generated matcher is JITted on its first candidate,
/// after the regex's 100 ms deadline has started, and that deadline is checked as soon as the candidate fails, so a
/// stalled runner holds a line back over a compile the AOT binary never does. Class order fixes which test that hits.</summary>
public static class WatcherPatternWarmup {
    // The vendor token comes first because its pattern is anchored to where the scan starts.
    internal const string Sample =
        """
        ghp_abcdefghij0123456789
        AKIAABCDEFGHIJKLMNOP
        -----BEGIN PRIVATE KEY-----
        abc
        -----END PRIVATE KEY-----
        authorization: Bearer abc
        https://example.test/?token=abc
        https://user:pass@example.test
        "password":"abc"
        API_KEY=abc
        secret: abcdefghij
        token abcdefghijklmnopqrst
        Password=abc;
        """;

    internal static IEnumerable<Regex> Patterns =>
        typeof(SecretPatterns).GetProperties()
            .Where(p => p.PropertyType == typeof(Regex))
            .Select(p => (Regex)p.GetValue(SecretRedactor.WatcherPatterns)!);

    [Before(Assembly)]
    public static void CompileWatcherPatterns() {
        foreach (var pattern in Patterns) {
            try {
                pattern.Replace(Sample, "");
            } catch (RegexMatchTimeoutException) {
                // A compile that outran the deadline still finished, and that is all this is for.
            }
        }
    }
}
