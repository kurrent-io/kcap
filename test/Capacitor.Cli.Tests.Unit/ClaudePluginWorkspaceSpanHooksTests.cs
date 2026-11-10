using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>The plugin manifest runs the span hook synchronously wherever a baseline must exist before the
/// agent's next step: at the prompt, at the turn's end, and right after an <c>EnterWorktree</c>.</summary>
public class ClaudePluginWorkspaceSpanHooksTests {
    static JsonNode Manifest([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx"))) dir = Path.GetDirectoryName(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "kcap", "hooks", "hooks.json")))!["hooks"]!;
    }

    static List<(string? Matcher, JsonNode Hook)> SpanEntries(string eventName) =>
        Manifest()[eventName]!.AsArray()
            .SelectMany(entry => entry!["hooks"]!.AsArray().Select(h => (entry["matcher"]?.GetValue<string>(), h!)))
            .Where(e => e.Item2["command"]!.GetValue<string>().Contains("--workspace-span"))
            .ToList();

    [Test]
    [Arguments("UserPromptSubmit")]
    [Arguments("Stop")]
    public async Task Each_turn_boundary_runs_the_span_hook_synchronously(string eventName) {
        var entries = SpanEntries(eventName);

        await Assert.That(entries).HasSingleItem();
        await Assert.That(entries[0].Hook["async"]).IsNull();
    }

    [Test]
    public async Task EnterWorktree_alone_runs_it_synchronously_after_the_tool() {
        var entries = SpanEntries("PostToolUse");

        await Assert.That(entries).HasSingleItem();
        await Assert.That(entries[0].Matcher).IsEqualTo("EnterWorktree");
        await Assert.That(entries[0].Hook["async"]).IsNull();
    }
}
