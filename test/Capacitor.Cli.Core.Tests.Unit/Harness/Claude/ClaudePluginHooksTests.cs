using Capacitor.Cli.Core.Harness.Claude;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Claude;

public class ClaudePluginHooksTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string ExecForm  = """{"type":"command","command":"kcap.exe","args":["hook","--claude"]}""";
    const string ShellForm = """{"type":"command","command":"kcap hook --claude"}""";

    string Plugin(params string[] hooks) {
        Tmp.CreateFile(["plugin", "hooks", "hooks.json"],
            """{"hooks":{"PreToolUse":[{"matcher":"*","hooks":[""" + string.Join(",", hooks) + "]}]}}");

        return Tmp.PathTo("plugin");
    }

    [Test]
    public async Task Hooks_that_all_name_a_program_and_its_args_start_without_a_shell() {
        await Assert.That(ClaudePluginHooks.StartThroughShell(Plugin(ExecForm, ExecForm))).IsFalse();
    }

    [Test]
    public async Task One_shell_form_hook_starts_through_a_shell() {
        await Assert.That(ClaudePluginHooks.StartThroughShell(Plugin(ExecForm, ShellForm))).IsTrue();
    }

    /// <summary>The warning this drives stays on when the plugin cannot be read, rather than claiming a fast start.</summary>
    [Test]
    public async Task A_missing_manifest_counts_as_shell_form() {
        await Assert.That(ClaudePluginHooks.StartThroughShell(Tmp.PathTo("absent"))).IsTrue();
    }

    [Test]
    public async Task A_malformed_manifest_counts_as_shell_form() {
        Tmp.CreateFile(["plugin", "hooks", "hooks.json"], "{ not json");

        await Assert.That(ClaudePluginHooks.StartThroughShell(Tmp.PathTo("plugin"))).IsTrue();
    }

    /// <summary>Valid JSON of the wrong shape must not throw: a string index on an array does.</summary>
    [Test]
    [Arguments("[]")]
    [Arguments("""{"hooks":{"PreToolUse":[[]]}}""")]
    [Arguments("""{"hooks":{"PreToolUse":[{"hooks":[[]]}]}}""")]
    public async Task A_manifest_of_the_wrong_shape_counts_as_shell_form(string json) {
        Tmp.CreateFile(["plugin", "hooks", "hooks.json"], json);

        await Assert.That(ClaudePluginHooks.StartThroughShell(Tmp.PathTo("plugin"))).IsTrue();
    }
}
