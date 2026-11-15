using System.Text.Json.Nodes;
using Capacitor.Cli.Harness.Claude;

namespace Capacitor.Cli.Tests.Unit.Harness.Claude;

public class ClaudeHookOutputTests {
    static string Context(string eventName, string text) =>
        new JsonObject { ["hookSpecificOutput"] = new JsonObject { ["hookEventName"] = eventName, ["additionalContext"] = text } }.ToJsonString();

    /// <summary>Claude reads one JSON value from a hook's stdout, so two handlers' context must arrive as one object
    /// carrying both texts, or the reader loses both.</summary>
    [Test]
    public async Task Two_handlers_context_becomes_one_object_carrying_both() {
        var merged = ClaudeHookOutput.Merge([Context("UserPromptSubmit", "title"), "", Context("UserPromptSubmit", "notices")], TextWriter.Null);

        var output = JsonNode.Parse(merged!)!["hookSpecificOutput"]!;
        await Assert.That(output["hookEventName"]!.GetValue<string>()).IsEqualTo("UserPromptSubmit");
        await Assert.That(output["additionalContext"]!.GetValue<string>()).IsEqualTo("title\n\nnotices");
    }

    [Test]
    public async Task Nothing_written_is_nothing_to_write() {
        await Assert.That(ClaudeHookOutput.Merge(["", "\n"], TextWriter.Null)).IsNull();
    }

    [Test]
    public async Task A_line_that_is_not_an_object_goes_to_stderr_and_spares_the_rest() {
        var stderr = new StringWriter();

        var merged = ClaudeHookOutput.Merge(["not json", Context("PostToolUse", "plan")], stderr);

        await Assert.That(JsonNode.Parse(merged!)!["hookSpecificOutput"]!["additionalContext"]!.GetValue<string>()).IsEqualTo("plan");
        await Assert.That(stderr.ToString()).Contains("not json");
    }
}
