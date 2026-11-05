using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>
/// Covers the Copilot hooks writer (<see cref="PluginCommand.InstallCopilotHooks"/> /
/// <see cref="PluginCommand.RemoveCopilotHooks"/>), the installer marker
/// helpers, and the parser. Unlike Cursor/Codex, kcap owns its own file under
/// <c>~/.copilot/hooks/</c> (Copilot merges all *.json in that dir), so there
/// is no user-entry preservation to test — install is a wholesale write and
/// remove is a delete.
/// </summary>
public class CopilotHooksTests {
    [Test]
    public async Task fresh_install_writes_all_subscribed_events_with_embedded_event_names() {
        using var tmp = new TempDir();
        var hooksPath = tmp.PathTo("hooks", "kcap.json");

        var ok = PluginCommand.InstallCopilotHooks(hooksPath);
        await Assert.That(ok).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        await Assert.That(root["version"]!.GetValue<int>()).IsEqualTo(1);

        var hooks = root["hooks"]!.AsObject();

        foreach (var evt in new[] { "sessionStart", "sessionEnd", "agentStop", "notification" }) {
            var entries = hooks[evt]!.AsArray();
            await Assert.That(entries.Count).IsEqualTo(1);

            var entry = entries[0]!.AsObject();
            await Assert.That(entry["type"]!.GetValue<string>()).IsEqualTo("command");
            // Copilot payloads carry no uniform event-name field, so the
            // command embeds the event for the dispatcher.
            await Assert.That(entry["command"]!.GetValue<string>()).IsEqualTo($"kcap hook --copilot --event {evt}");
            await Assert.That(entry["timeoutSec"]!.GetValue<int>()).IsEqualTo(30);
        }
    }

    [Test]
    public async Task install_writes_marker_and_remove_deletes_file_and_marker() {
        using var tmp = new TempDir();
        var hooksPath = tmp.PathTo("hooks", "kcap.json");

        PluginCommand.InstallCopilotHooks(hooksPath);

        await Assert.That(CopilotHooksInstaller.IsInstalled(hooksPath)).IsTrue();
        await Assert.That(CopilotHooksInstaller.ReadMarker(hooksPath)).IsNotNull();

        var removed = PluginCommand.RemoveCopilotHooks(hooksPath);

        await Assert.That(removed).IsTrue();
        await Assert.That(File.Exists(hooksPath)).IsFalse();
        await Assert.That(CopilotHooksInstaller.IsInstalled(hooksPath)).IsFalse();
        await Assert.That(CopilotHooksInstaller.ReadMarker(hooksPath)).IsNull();
    }

    [Test]
    public async Task remove_without_install_reports_nothing_removed() {
        using var tmp = new TempDir();
        var hooksPath = tmp.PathTo("hooks", "kcap.json");

        await Assert.That(PluginCommand.RemoveCopilotHooks(hooksPath)).IsFalse();
    }

    [Test]
    public async Task is_installed_detects_pre_marker_installs_from_file_content() {
        using var tmp = new TempDir();
        var hooksDir  = tmp.PathTo("hooks");
        var hooksPath = Path.Combine(hooksDir, "kcap.json");

        Directory.CreateDirectory(hooksDir);
        await File.WriteAllTextAsync(hooksPath, """
            {"version":1,"hooks":{"sessionStart":[{"type":"command","command":"kcap hook --copilot --event sessionStart"}]}}
        """);

        await Assert.That(CopilotHooksInstaller.IsInstalled(hooksPath)).IsTrue();
    }

    /// <summary>VS Code and Copilot run <c>command</c> in PowerShell on Windows, where a quoted path only runs
    /// behind the call operator, and in a POSIX shell elsewhere.</summary>
    [Test]
    [Arguments("/opt/homebrew/bin/kcap", false, "'/opt/homebrew/bin/kcap' hook --copilot --event sessionStart")]
    [Arguments("/Users/a b/kcap", false, "'/Users/a b/kcap' hook --copilot --event sessionStart")]
    [Arguments("/Users/o'brien/kcap", false, @"'/Users/o'\''brien/kcap' hook --copilot --event sessionStart")]
    [Arguments(@"C:\Users\A B\AppData\Local\kcap\current\bin\kcap.exe", true, @"& 'C:\Users\A B\AppData\Local\kcap\current\bin\kcap.exe' hook --copilot --event sessionStart")]
    [Arguments(@"C:\Users\o'brien\kcap.exe", true, @"& 'C:\Users\o''brien\kcap.exe' hook --copilot --event sessionStart")]
    [Arguments("kcap", false, "kcap hook --copilot --event sessionStart")]
    [Arguments("kcap", true, "kcap hook --copilot --event sessionStart")]
    public async Task The_hook_command_quotes_the_binary_for_the_shell_that_runs_it(string binary, bool windows, string expected) =>
        await Assert.That(CopilotHooksParser.HookCommandLine("sessionStart", binary, windows)).IsEqualTo(expected);

    [Test]
    public async Task A_written_command_runs_the_binary_through_a_real_shell_from_a_path_with_a_space_and_a_quote() {
        if (OperatingSystem.IsWindows()) return;

        using var tmp = new TempDir();
        var seen   = tmp.PathTo("seen.txt");
        var binary = tmp.CreateExecutable(Path.Combine("o'brien app", "kcap"), $"#!/bin/sh\necho \"$@\" > '{seen}'\n");

        using var shell = System.Diagnostics.Process.Start("/bin/sh", ["-c", CopilotHooksParser.HookCommandLine("agentStop", binary, windows: false)]);
        await shell.WaitForExitAsync();

        await Assert.That(shell.ExitCode).IsEqualTo(0);
        await Assert.That((await File.ReadAllTextAsync(seen)).Trim()).IsEqualTo("hook --copilot --event agentStop");
    }

    [Test]
    public async Task Install_writes_the_full_path_and_stays_recognised_as_kcaps() {
        using var tmp = new TempDir();
        var hooksPath = tmp.PathTo("hooks", "kcap.json");

        await Assert.That(PluginCommand.InstallCopilotHooks(hooksPath, () => "/opt/kcap app/kcap")).IsTrue();

        var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();
        await Assert.That(root["hooks"]!["agentStop"]![0]!["command"]!.GetValue<string>())
            .Contains("'/opt/kcap app/kcap' hook --copilot --event agentStop");
        await Assert.That(CopilotHooksParser.HasCapacitorHooksFor(root, CopilotHooksParser.CopilotHookEvents)).IsTrue();
    }

    /// <summary>A process that is not kcap, such as an embedding host, must not end up as the hook's command.</summary>
    [Test]
    [Arguments("/opt/kcap/bin/kcap", false)]
    [Arguments(@"C:\Users\a\AppData\Local\kcap\current\bin\kcap.exe", false)]
    [Arguments("/usr/local/share/dotnet/dotnet", true)]
    [Arguments(null, true)]
    public async Task Only_a_process_that_is_kcap_lifts_the_PATH_requirement(string? processPath, bool needsPath) =>
        await Assert.That(PluginCommand.CopilotHooksNeedPath(() => processPath)).IsEqualTo(needsPath);

    [Test]
    public async Task parser_matches_command_bash_and_powershell_fields() {
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"command":"kcap hook --copilot --event sessionStart"}"""))).IsTrue();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"bash":"kcap hook --copilot --event sessionEnd"}"""))).IsTrue();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"powershell":"kcap hook --copilot --event agentStop"}"""))).IsTrue();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"command":"'/opt/kcap app/kcap' hook --copilot --event agentStop"}"""))).IsTrue();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"command":"& 'C:\\A B\\kcap.exe' hook --copilot --event agentStop"}"""))).IsTrue();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"command":"'/opt/other' hook --copilot"}"""))).IsFalse();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"command":"/usr/local/bin/other"}"""))).IsFalse();
        await Assert.That(CopilotHooksParser.EntryReferencesCapacitorCopilotHook(
            JsonNode.Parse("""{"url":"https://hooks.example.com"}"""))).IsFalse();
    }

    [Test]
    public async Task has_capacitor_hooks_for_requires_every_event() {
        using var tmp = new TempDir();
        var hooksPath = tmp.PathTo("hooks", "kcap.json");

        PluginCommand.InstallCopilotHooks(hooksPath);

        var root = JsonNode.Parse(await File.ReadAllTextAsync(hooksPath))!.AsObject();

        await Assert.That(CopilotHooksParser.HasCapacitorHooksFor(root, CopilotHooksParser.CopilotHookEvents)).IsTrue();
        await Assert.That(CopilotHooksParser.HasCapacitorHooksFor(root, ["sessionStart", "someFutureEvent"])).IsFalse();
    }
}
