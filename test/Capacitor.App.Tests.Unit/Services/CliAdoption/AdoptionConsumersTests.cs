using System.Diagnostics;
using System.Security;
using System.Text.Json.Nodes;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit.Services.CliAdoption;

public class AdoptionConsumersTests {
    static readonly DateTime Cutover = new(2026, 11, 5, 12, 0, 0, DateTimeKind.Utc);
    static readonly DateTime Before  = Cutover.AddMinutes(-30);
    static readonly DateTime After   = Cutover.AddMinutes(30);

    [TempDir] public required TempDir Tmp { get; init; }

    string Bundle => Tmp.GetResolvedPath("Kcap.app");

    HarnessRegistry Harnesses => TestHarnesses.Under(new UserHome(Tmp.PathTo("home")));

    OtherCliInstall Npm(string prefix = "prefix") {
        var scope    = Tmp.GetResolvedPath(prefix, "lib", "node_modules", "@kurrent");
        var root     = Path.Combine(scope, "kcap");
        var platform = Path.Combine(scope, "kcap-darwin-arm64");
        return new OtherCliInstall(OtherCliInstallKind.Npm, root, "1.2.3", Path.Combine(platform, "bin", "kcap"),
            Path.Combine(platform, "bin", "kcap-daemon"), "id", [root, platform]);
    }

    string? Holder(OtherCliInstall install, ProcessFact[]? processes = null, ReferenceSite[]? sites = null, DateTime? cutover = null) =>
        AdoptionConsumers.Holder(install, cutover ?? Cutover, processes ?? [], sites ?? [], Harnesses, Bundle);

    [Test]
    public async Task An_mcp_server_running_from_the_install_holds_it() {
        var install = Npm();

        await Assert.That(Holder(install, [new(40, "/usr/bin/login", null, Before), new(41, install.CliBinary, null, After)]))
            .IsEqualTo("pid:41");
        await Assert.That(Holder(install, [new(42, "/opt/homebrew/bin/node", ["node", Path.Combine(install.Root, "bin", "kcap.js"), "mcp", "review"], After)]))
            .IsEqualTo("pid:42");
        await Assert.That(Holder(install, [new(43, Path.Combine(Bundle, "Contents", "MacOS", "kcap"), null, Before)])).IsNull();
    }

    /// <summary>A runtime is named only by its arguments; a native binary needs none to be told apart.</summary>
    [Test]
    public async Task A_runtime_whose_arguments_could_not_be_read_holds_it() {
        var install = Npm();

        await Assert.That(Holder(install, [new(44, "/opt/homebrew/bin/node", null, After)])).IsEqualTo("pid:44");
        await Assert.That(Holder(install, [new(45, "/usr/bin/python3", null, After)])).IsEqualTo("pid:45");
        await Assert.That(Holder(install, [new(46, "/usr/bin/login", null, Before)])).IsNull();
    }

    [Test]
    public async Task A_plist_naming_the_install_through_xml_escapes_holds_it() {
        var install = Npm("a&b");
        var plist   = $"<plist><dict><key>Program</key><string>{SecurityElement.Escape(install.CliBinary)}</string></dict></plist>";

        await Assert.That(plist).DoesNotContain(install.Root);
        await Assert.That(Holder(install, sites: [new("/h/Library/LaunchAgents/com.example.agent.plist", plist, true)]))
            .IsEqualTo("site:/h/Library/LaunchAgents/com.example.agent.plist");
    }

    [Test]
    public async Task A_harness_started_before_the_cutover_holds_it_and_one_after_does_not() {
        var install = Npm();

        await Assert.That(Holder(install, [new(50, "/opt/homebrew/bin/codex", null, Before)])).IsEqualTo("pid:50");
        await Assert.That(Holder(install, [new(51, "/opt/homebrew/bin/codex", null, Cutover)])).IsEqualTo("pid:51");
        await Assert.That(Holder(install, [new(52, "/opt/homebrew/bin/codex", null, After)])).IsNull();
    }

    [Test]
    public async Task Harnesses_under_node_and_bun_and_a_retitled_native_Claude_are_recognised() {
        var install = Npm();
        ProcessFact[] harnesses = [
            new(60, "/opt/homebrew/Cellar/node/24.1.0/bin/node", ["node", "/opt/homebrew/bin/gemini"], Before),
            new(61, "/opt/homebrew/Cellar/node/24.1.0/bin/node",
                ["node", "--no-warnings", "/opt/homebrew/lib/node_modules/@google/gemini-cli/dist/index.js"], Before),
            new(62, "/Users/u/.bun/bin/bun", ["bun", "--smol", "/Users/u/.bun/bin/codex"], Before),
            new(63, "/Users/u/.local/share/claude/versions/2.1.31", null, Before),
            new(64, "/opt/homebrew/Cellar/node/24.1.0/bin/node", ["claude"], Before),
        ];

        foreach (var harness in harnesses)
            await Assert.That(Holder(install, [harness])).IsEqualTo($"pid:{harness.Pid}");
        await Assert.That(Holder(install, [new(65, "/opt/homebrew/bin/node", ["node", "/Users/u/dev/server.js"], Before)])).IsNull();
    }

    /// <summary>Vibe is a Python interpreter, named only by the title it sets or the script it runs.</summary>
    [Test]
    public async Task A_python_session_running_vibe_holds_it() {
        var install = Npm();
        const string python = "/Users/u/.local/share/uv/python/cpython-3.12.7-macos-aarch64-none/bin/python3.12";

        await Assert.That(Holder(install, [new(80, python, ["Vibe CLI"], Before)])).IsEqualTo("pid:80");
        await Assert.That(Holder(install, [new(81, python, ["/Users/u/.local/share/uv/tools/mistral-vibe/bin/python", "/Users/u/.local/bin/vibe"], Before)]))
            .IsEqualTo("pid:81");
        await Assert.That(Holder(install, [new(82, python, [python, "/Users/u/dev/tool.py"], Before)])).IsNull();
    }

    /// <summary>Cursor's CLI is node under its own <c>versions/&lt;v&gt;/</c>, started through an <c>agent</c> link.</summary>
    [Test]
    public async Task A_node_session_under_cursor_agents_versions_folder_holds_it() {
        var install = Npm();
        const string version = "/Users/u/.local/share/cursor-agent/versions/2025.11.04-8a1b2c3";

        await Assert.That(Holder(install, [new(83, $"{version}/node", ["/Users/u/.local/bin/agent", "--use-system-ca", $"{version}/index.js"], Before)]))
            .IsEqualTo("pid:83");
        await Assert.That(Holder(install, [new(84, "/Users/u/.nvm/versions/node/v22.1.0/bin/node", ["node", "/Users/u/dev/server.js"], Before)]))
            .IsNull();
    }

    [Test]
    public async Task Arguments_are_read_for_javascript_runtimes_and_python_only() {
        await Assert.That(ProcessFacts.ReadsArgs("/opt/homebrew/bin/node")).IsTrue();
        await Assert.That(ProcessFacts.ReadsArgs("/Users/u/.bun/bin/bun")).IsTrue();
        await Assert.That(ProcessFacts.ReadsArgs("/opt/homebrew/Cellar/python@3.12/3.12.7/bin/python3.12")).IsTrue();
        await Assert.That(ProcessFacts.ReadsArgs("/Library/Frameworks/Python.framework/Versions/3.12/Resources/Python.app/Contents/MacOS/Python")).IsTrue();
        await Assert.That(ProcessFacts.ReadsArgs("/usr/bin/login")).IsFalse();
    }

    /// <summary>Its deletion keeps the rest of the root, which can hold the user's own files.</summary>
    [Test]
    public async Task A_script_install_is_named_only_by_what_its_deletion_removes() {
        var root    = Tmp.GetResolvedPath("kcap-home");
        var current = Path.Combine(root, "current");
        var install = new OtherCliInstall(OtherCliInstallKind.Script, root, "1.0.0", Path.Combine(current, "bin", "kcap"),
            Path.Combine(current, "bin", "kcap-daemon"), "id", [Path.Combine(root, "versions"), current, Path.Combine(root, "install.json")]);

        await Assert.That(Holder(install, sites: [new("/h/.gitconfig", $"[include]\n\tpath = {root}/gitconfig\n", true)])).IsNull();
        await Assert.That(Holder(install, sites: [new("/h/.codex/config.toml", $"command = \"{install.CliBinary}\"", true)]))
            .IsEqualTo("site:/h/.codex/config.toml");
    }

    /// <summary>Cursor's IDE is the Cursor kcap hooks into, though its CLI is <c>cursor-agent</c>.</summary>
    [Test]
    public async Task A_harness_ide_started_before_the_cutover_holds_it() {
        var install = Npm();

        await Assert.That(Holder(install, [new(66, "/Applications/Cursor.app/Contents/MacOS/Cursor", null, Before)])).IsEqualTo("pid:66");
        await Assert.That(Holder(install, [new(67, "/Applications/Cursor.app/Contents/MacOS/Cursor", null, After)])).IsNull();
    }

    /// <summary>Pinned under a zone east of UTC, where a start time left in local time reads as after the cutover.</summary>
    [Test, NotInParallel, ExcludeOn(OS.Windows)]
    public async Task Start_times_compare_in_utc() {
        try {
            using var tz = EnvScope.Exclusive("TZ", "Asia/Tokyo");
            TimeZoneInfo.ClearCachedData();
            var install     = Npm();
            var beforeLocal = Before.ToLocalTime();
            await Assert.That(beforeLocal.Ticks).IsGreaterThan(Cutover.Ticks);

            await Assert.That(Holder(install, [new(70, "/opt/homebrew/bin/codex", null, beforeLocal)])).IsEqualTo("pid:70");
            await Assert.That(Holder(install, [new(71, "/opt/homebrew/bin/codex", null, After)], cutover: Cutover.ToLocalTime())).IsNull();
        } finally {
            TimeZoneInfo.ClearCachedData();
        }
    }

    [Test]
    public async Task A_reference_through_the_real_path_of_a_symlinked_prefix_holds_it() {
        Tmp.CreateDir("real-prefix");
        Directory.CreateSymbolicLink(Tmp.GetResolvedPath("prefix"), Tmp.GetResolvedPath("real-prefix"));
        var install = Npm("prefix");
        var plugin  = Tmp.GetResolvedPath("real-prefix", "lib", "node_modules", "@kurrent", "kcap", "plugin");

        await Assert.That(Holder(install, sites: [new("/h/.claude/settings.json", $$$"""{"source":{"path":"{{{plugin}}}"}}""", true)]))
            .IsEqualTo("site:/h/.claude/settings.json");
        await Assert.That(Holder(install, sites: [new("/h/.claude/settings.json", $$$"""{"source":{"path":"{{{Bundle}}}/plugin"}}""", true)]))
            .IsNull();
    }

    [Test]
    public async Task A_reference_with_its_non_ascii_escaped_as_json_writes_it_holds_it() {
        var install = Npm("bjørn");
        var written = new JsonObject { ["command"] = install.CliBinary }.ToJsonString();

        await Assert.That(written).Contains("\\u00F8");
        await Assert.That(Holder(install, sites: [new("/h/.cursor/mcp.json", written, true)]))
            .IsEqualTo("site:/h/.cursor/mcp.json");
    }

    [Test]
    public async Task An_unreadable_site_holds_it() {
        var install = Npm();

        await Assert.That(Holder(install, sites: [new("/h/.codex/hooks.json", "{}", true), new("/h/.codex/config.toml", null, false)]))
            .IsEqualTo("site:/h/.codex/config.toml");
        await Assert.That(Holder(install, sites: [new("/h/.codex/config.toml", null, true)])).IsNull();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task This_users_processes_include_the_test_host_with_a_utc_start_time() {
        var facts = ProcessFacts.ForCurrentUser();
        var self  = facts.SingleOrDefault(p => p.Pid == Environment.ProcessId);
        using var current = Process.GetCurrentProcess();

        await Assert.That(self).IsNotNull();
        await Assert.That(self!.StartedUtc.Kind).IsEqualTo(DateTimeKind.Utc);
        await Assert.That(Math.Abs((self.StartedUtc - current.StartTime.ToUniversalTime()).TotalSeconds)).IsLessThan(1);
        await Assert.That(self.Executable).IsEqualTo(ProcessExecutable.Of(Environment.ProcessId));
        await Assert.That(facts.Any(p => p.Pid == 1)).IsFalse();
    }
}
