using System.Text.Json;
using Capacitor.Models.Transcripts.Harness.Codex;
using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Codex;

public class CodexPreludeTests {
    static IEnumerable<(string Line, string Text, IReadOnlyList<string>? Kinds, bool Human)> Fixture() {
        foreach (var line in File.ReadLines(Path.Combine(AppContext.BaseDirectory, "fixtures", "codex", "codex-user-lines.jsonl"))) {
            using var doc = JsonDocument.Parse(line);
            var payload = doc.RootElement.GetProperty("payload");
            var text = payload.GetProperty("content")[0].GetProperty("text").GetString()!;
            IReadOnlyList<string>? kinds = payload.TryGetProperty("internal_chat_message_metadata_passthrough", out var pass)
                ? pass.GetProperty("content_item_kinds").EnumerateArray().Select(k => k.GetString()!).ToList()
                : null;

            yield return (line, text, kinds, doc.RootElement.GetProperty("expect_human").GetBoolean());
        }
    }

    /// The server pins the same file: a turn this side declares as human must be one the server counts as
    /// human, or a declared turn set never verifies.
    [Test]
    public async Task Codex_user_lines_classify_as_the_fixture_says() {
        foreach (var (line, text, kinds, human) in Fixture())
            await Assert.That(CodexPrelude.IsHuman(text, kinds)).IsEqualTo(human).Because(line);
    }

    /// The judge can take the classifier's verdict for these lines without a declared turn changing.
    [Test]
    public async Task The_classifier_agrees_with_the_fixture() {
        foreach (var (line, text, kinds, human) in Fixture()) {
            var cls = UserMessageClassifier.Classify(text, new UserMessageOrigin { ContentItemKinds = kinds }, "codex");

            await Assert.That(cls.Kind == UserMessageKind.Person).IsEqualTo(human).Because(line);
        }
    }

    [Test]
    public async Task A_prelude_before_the_persons_words_is_stripped_to_them() {
        const string text = "<recommended_plugins>\n- pages\n</recommended_plugins>\n\nFix the projector rebuild";

        await Assert.That(CodexPrelude.StripLeading(text).Trim()).IsEqualTo("Fix the projector rebuild");
        await Assert.That(CodexPrelude.IsMatch(text)).IsFalse();
    }

    [Test]
    public async Task An_unterminated_block_swallows_the_rest() =>
        await Assert.That(CodexPrelude.StripLeading("<environment_context>\n  <cwd>/repo</cwd>\nFix it")).IsEqualTo("");
}
