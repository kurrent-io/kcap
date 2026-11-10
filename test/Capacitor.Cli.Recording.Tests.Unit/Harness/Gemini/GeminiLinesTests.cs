using Capacitor.Cli.Recording.Harness.Gemini;

namespace Capacitor.Cli.Recording.Tests.Unit.Harness.Gemini;

public class GeminiLinesTests {
    // Mirror of the GeminiTranscriptNormalizer skip rules.
    [Test]
    [Arguments("""{"sessionId":"s","projectHash":"h","kind":"main"}""", false)]                                                              // header
    [Arguments("""{"$set":{"lastUpdated":"t"}}""", false)]                                                                                    // mutation op
    [Arguments("""{"id":"u","type":"user","content":[{"text":"<session_context>x</session_context>"}]}""", false)]                            // bootstrap
    [Arguments("""{"id":"u","type":"user","content":[{"functionResponse":{"id":"f","name":"read","response":{"output":"x"}}}]}""", false)]    // tool-result echo
    [Arguments("""{"id":"u","type":"user","content":[{"text":"real prompt"}]}""", true)]                                                      // real user prompt
    [Arguments("""{"id":"g","type":"gemini","content":"answer"}""", true)]                                                                    // gemini turn
    public async Task import_relevant_line_mirrors_normalizer_skips(string line, bool expected) {
        await Assert.That(GeminiLines.IsImportRelevantLine(line)).IsEqualTo(expected);
    }
}
