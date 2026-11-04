using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary><c>usage-limit</c> is OpenCode's stderr from a real run against a provider that had hit its limit,
/// captured at INFO so the lines around the error are there too.</summary>
public class OpenCodeLimitLogTests {
    static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "opencode-headless", "usage-limit.stderr.log");

    [Test]
    public async Task The_captured_stream_error_is_the_only_line_matched_and_gives_its_message() {
        var matches = File.ReadLines(Fixture).Select(OpenCodeLimitLog.Match).OfType<string>().ToList();

        await Assert.That(matches).IsEquivalentTo([
            "AI_APICallError: Free usage limit reached. Free models work in OpenCode (https://opencode.ai); subscribe to OpenCode Go to keep going: https://opencode.ai/go",
        ]);
    }

    [Test]
    [Arguments("""level=ERROR message="stream error" error.error="429 Too Many Requests: rate limit exceeded" """)]
    [Arguments("""level=ERROR message="stream error" error.error="Your credit balance is too low" """)]
    [Arguments("""level=ERROR message="stream error" error.error="quota exceeded, said \"the provider\"" """)]
    public async Task A_stream_error_carrying_a_spend_marker_matches(string line) =>
        await Assert.That(OpenCodeLimitLog.Match(line)).IsNotNull();

    [Test]
    public async Task An_escaped_quote_in_the_message_is_unescaped() =>
        await Assert.That(OpenCodeLimitLog.Match("""level=ERROR message="stream error" error.error="quota \"x\" hit" next=1"""))
            .IsEqualTo("quota \"x\" hit");

    [Test]
    [Arguments("""level=ERROR message="stream error" error.error="AI_APICallError: invalid x-api-key" """)]
    [Arguments("""level=INFO message="stream error" error.error="usage limit reached" """)]
    [Arguments("""level=ERROR message="other error" error.error="usage limit reached" """)]
    [Arguments("""level=ERROR message="stream error" """)]
    [Arguments("usage limit reached")]
    [Arguments("")]
    public async Task Anything_else_does_not_match(string line) =>
        await Assert.That(OpenCodeLimitLog.Match(line)).IsNull();
}
