using System.Runtime.CompilerServices;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Unit;

/// <summary>The plugin manifest runs the span hook synchronously wherever a baseline must exist before the
/// agent's next step: at the prompt, at the turn's end, and right after an <c>EnterWorktree</c>. At the turn's
/// end it rides the main Stop hook's process.</summary>
public class ClaudePluginWorkspaceSpanHooksTests {
    static JsonNode Manifest([CallerFilePath] string here = "") {
        var dir = Path.GetDirectoryName(here);
        while (dir is not null && !File.Exists(Path.Combine(dir, "Capacitor.slnx"))) dir = Path.GetDirectoryName(dir);
        return JsonNode.Parse(File.ReadAllText(Path.Combine(dir!, "kcap", "hooks", "hooks.json")))!["hooks"]!;
    }

    static List<(string? Matcher, JsonNode Hook)> SpanEntries(string eventName) =>
        Manifest()[eventName]!.AsArray()
            .SelectMany(entry => entry!["hooks"]!.AsArray().Select(h => (entry["matcher"]?.GetValue<string>(), h!)))
            .Where(e => e.Item2["command"]!.GetValue<string>() is var command && (command.Contains("--workspace-span") || command.Contains("--stop-spans")))
            .ToList();

    [Test]
    [Arguments("UserPromptSubmit")]
    [Arguments("Stop")]
    public async Task Each_turn_boundary_runs_the_span_hook_synchronously(string eventName) {
        var entries = SpanEntries(eventName);

        await Assert.That(entries).HasSingleItem();
        await Assert.That(entries[0].Hook["async"]).IsNull();
    }

    static List<string> SyncCommands(string eventName, string? tool = null) =>
        Manifest()[eventName]!.AsArray()
            .Where(entry => tool is null || entry!["matcher"]?.GetValue<string>() is not { } m
                         || m == "*" || m.Split('|').Contains(tool))
            .SelectMany(entry => entry!["hooks"]!.AsArray())
            .Where(h => h!["async"] is null && h["if"] is null)
            .Select(h => h!["command"]!.GetValue<string>())
            .ToList();

    /// <summary>Each process is a full kcap start, and Claude waits on every synchronous one: an event's side hooks
    /// share one process instead of starting one each.</summary>
    [Test]
    [Arguments("UserPromptSubmit", null, 1)]
    [Arguments("Stop", null, 1)]
    [Arguments("PostToolUse", "Edit", 1)]
    [Arguments("PostToolUse", "Write", 1)]
    public async Task An_event_starts_one_synchronous_process_for_its_side_hooks(string eventName, string? tool, int processes) {
        await Assert.That(SyncCommands(eventName, tool)).Count().IsEqualTo(processes);
    }

    /// <summary>A kcap that predates combined flags runs only the first side hook it knows and returns, so the turn's
    /// end must not name one: there it would take over the process and the Stop would never be posted.</summary>
    [Test]
    public async Task The_stop_process_is_the_main_hook_to_a_kcap_that_predates_combined_flags() {
        var stop = SyncCommands("Stop").Single();

        foreach (var side in new[] { "--plan-read", "--task-list", "--plan-edit", "--workspace-span", "--pass-notices", "--title-prompt" })
            await Assert.That(stop).DoesNotContain(side);
    }

    [Test]
    public async Task EnterWorktree_alone_runs_it_synchronously_after_the_tool() {
        var entries = SpanEntries("PostToolUse");

        await Assert.That(entries).HasSingleItem();
        await Assert.That(entries[0].Matcher).IsEqualTo("EnterWorktree");
        await Assert.That(entries[0].Hook["async"]).IsNull();
    }
}
