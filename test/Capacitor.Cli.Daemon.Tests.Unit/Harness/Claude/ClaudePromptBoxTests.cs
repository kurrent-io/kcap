using Capacitor.Cli.Daemon.Harness.Claude;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Claude;

/// Pins when Claude Code's prompt box reads as on screen: drawn between its two rules, with or without
/// typed text, and not while a dialog stands in its place.
public class ClaudePromptBoxTests {
    static readonly string Rule = new('─', 120);

    [Test]
    [Arguments(true, new[] { "● ready", "", "RULE", "❯ ", "RULE", "  ⏵⏵ auto mode on (shift+tab to cycle)" })]
    [Arguments(true, new[] { "RULE", "❯ a long message that the box", "  wraps onto a second row", "RULE" })]
    [Arguments(false, new[] { "❯ /model", "", "RULE", "  Select model", "  ❯ 1. Opus", "    2. Sonnet", "", "  Enter to confirm · Esc to cancel" })]
    [Arguments(false, new[] { "RULE", " Bash command", " Do you want to proceed?", " ❯ 1. Yes", "   2. No", "", " Esc to cancel · Tab to amend" })]
    [Arguments(false, new[] { "● ready", "" })]
    public async Task The_prompt_box_reads_as_shown_only_between_its_rules(bool shown, string[] rows) {
        var screen = string.Join('\n', rows.Select(r => r == "RULE" ? Rule : r));

        await Assert.That(ClaudePromptBox.IsShown(screen)).IsEqualTo(shown);
    }
}
