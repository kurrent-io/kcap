using Capacitor.Cli.Core.Processes;
using TUnit.Assertions.Enums;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Capacitor.Cli.Tests.Unit;

public class ProcessHelpersTests {
    [Test]
    public async Task IsProcessAlive_returns_true_for_current_process() {
        var alive = ProcessHelpers.IsProcessAlive(Environment.ProcessId);

        await Assert.That(alive).IsTrue();
    }

    [Test]
    public async Task IsProcessAlive_returns_false_for_pid_zero_or_one() {
        // pid 0 is invalid; pid 1 is init/launchd which we treat as "no parent" by convention.
        await Assert.That(ProcessHelpers.IsProcessAlive(0)).IsFalse();
        await Assert.That(ProcessHelpers.IsProcessAlive(1)).IsFalse();
    }

    [Test]
    public async Task IsProcessAlive_returns_false_for_negative_pid() {
        await Assert.That(ProcessHelpers.IsProcessAlive(-1)).IsFalse();
    }

    [Test]
    public async Task IsProcessAlive_transitions_to_false_after_child_exits() {
        // Use a long-running child and Kill() it explicitly: a fast-exit command can be
        // reaped by .NET's SIGCHLD handler before the alive assertion runs on a busy CI
        // scheduler, making `kill(pid, 0)` return ESRCH and the test fail intermittently.
        // Spawn the binary directly (no shell wrapper) so there's no descendant to orphan
        // when we kill the tracked pid.
        var psi = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
            ? new ProcessStartInfo("ping", "-n 30 127.0.0.1")
            : new ProcessStartInfo("/bin/sleep", "30");

        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError  = true;
        psi.UseShellExecute        = false;

        using var process = new Process();
        process.StartInfo = psi;
        process.Start();
        var pid = process.Id;

        await Assert.That(ProcessHelpers.IsProcessAlive(pid)).IsTrue();

        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();

        // After .NET reaps the killed child, kill(pid, 0) returns ESRCH. Poll briefly
        // in case the reaper runs slightly later than WaitForExitAsync's completion.
        var deadline = DateTime.UtcNow.AddSeconds(2);

        while (DateTime.UtcNow < deadline && ProcessHelpers.IsProcessAlive(pid)) {
            await Task.Delay(50);
        }

        await Assert.That(ProcessHelpers.IsProcessAlive(pid)).IsFalse();
    }

    [Test]
    public async Task GetParentPid_returns_a_live_process() {
        var ppid = ProcessHelpers.GetParentPid();

        await Assert.That(ppid).IsNotNull();
        await Assert.That(ProcessHelpers.IsProcessAlive(ppid!.Value)).IsTrue();
    }

    [Test]
    public async Task GetCodingAgentPid_returns_a_live_process() {
        // The whole point of GetCodingAgentPid is to identify a process that's
        // still alive when the watcher boots up — i.e. the long-lived coding
        // agent, not the short-lived hook executor. Anything it returns must
        // therefore answer "yes" to IsProcessAlive at the moment of the call.
        var pid = ProcessHelpers.GetCodingAgentPid();

        await Assert.That(pid).IsNotNull();
        await Assert.That(ProcessHelpers.IsProcessAlive(pid!.Value)).IsTrue();
    }

    [Test]
    public async Task GetCodingAgentPid_does_not_return_own_pid() {
        // Self-monitoring would let the watcher self-terminate as soon as it
        // starts. The Unix branch falls back to getppid when the process group
        // leader is the calling process itself.
        var pid = ProcessHelpers.GetCodingAgentPid();

        await Assert.That(pid).IsNotEqualTo(Environment.ProcessId);
    }

    [Test]
    public async Task GetCodingAgentPid_with_vendor_returns_a_live_non_self_process() {
        // With no claude/codex ancestor in the test host, the vendor-aware overload
        // must fall back to the legacy heuristic and still yield a live, non-self PID.
        var pid = ProcessHelpers.GetCodingAgentPid("claude");

        await Assert.That(pid).IsNotNull();
        await Assert.That(ProcessHelpers.IsProcessAlive(pid!.Value)).IsTrue();
        await Assert.That(pid).IsNotEqualTo(Environment.ProcessId);
    }

    [Test]
    public async Task GetProcessInfo_returns_ppid_and_name_for_current_process() {
        // Ancestry resolution needs the real parent pid and a nonempty executable name on every platform
        // so the watchdog can identify the durable coding-agent process.
        var info = ProcessHelpers.GetProcessInfo(Environment.ProcessId);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Value.ppid).IsEqualTo(ProcessHelpers.GetParentPid()!.Value);
        await Assert.That(info.Value.names.Count).IsGreaterThan(0);
        await Assert.That(info.Value.names.All(n => n.Length > 0)).IsTrue();
    }

    [Test]
    public async Task GetProcessCwd_round_trips_the_current_process() {
        // This IS the validation of the macOS proc_vnodepathinfo struct offsets — a wrong offset
        // returns garbage or null, never our own cwd, so a macOS release reshaping the struct
        // fails HERE rather than silently starving the Antigravity workspace fallback.
        // Windows deliberately returns null (no cheap same-user API; all callers fail open).
        if (OperatingSystem.IsWindows()) {
            await Assert.That(ProcessHelpers.GetProcessCwd(Environment.ProcessId)).IsNull();
            return;
        }

        var reported = ProcessHelpers.GetProcessCwd(Environment.ProcessId);

        await Assert.That(reported).IsNotNull();

        // Compare canonically and EXACTLY: the runner's cwd may be reached through a symlink
        // (/tmp vs /private/tmp on macOS) and the kernel reports the resolved path, so both
        // sides are pushed through realpath semantics before the equality. Anything looser
        // (EndsWith, an || chain) would let a wrong struct offset pass on garbage.
        static string Canonical(string p) {
            var info = new DirectoryInfo(p);
            return (info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? info.FullName)
                .TrimEnd(Path.DirectorySeparatorChar);
        }

#pragma warning disable RS0030 // the process's own directory is what this reports
        await Assert.That(Canonical(reported!)).IsEqualTo(Canonical(Directory.GetCurrentDirectory()));
#pragma warning restore RS0030
    }

    [Test]
    public async Task DecodeNulTerminated_is_bounded_and_rejects_malformed_regions() {
        // The exact scenario the bounded decode exists for: a full region with NO terminator must
        // read as null, never as a truncated path and never as a scan past the region.
        var unterminated = new byte[16];
        Array.Fill(unterminated, (byte)'a');
        await Assert.That(ProcessHelpers.DecodeNulTerminated(unterminated)).IsNull();

        // Empty-at-zero is "no cwd", not "".
        await Assert.That(ProcessHelpers.DecodeNulTerminated(new byte[8])).IsNull();

        // A properly terminated path decodes to exactly the bytes before the NUL — including
        // non-ASCII, since APFS paths are UTF-8.
        var ok = "/tmp/wörk\0garbage-after-nul"u8.ToArray();
        await Assert.That(ProcessHelpers.DecodeNulTerminated(ok)).IsEqualTo("/tmp/wörk");
    }

    [Test]
    public async Task GetProcessCwd_returns_null_for_invalid_pids() {
        await Assert.That(ProcessHelpers.GetProcessCwd(0)).IsNull();
        await Assert.That(ProcessHelpers.GetProcessCwd(-5)).IsNull();
        // PID far above any live process on a healthy system; a dead pid must read as null,
        // never throw — the fallback path runs inside a fail-open hook.
        await Assert.That(ProcessHelpers.GetProcessCwd(99_999_999)).IsNull();
    }

    [Test]
    public async Task GetProcessInfo_reports_parent_chain_reaching_a_live_ancestor() {
        // Walking ppid from this process must reach our real parent and report it alive.
        if (OperatingSystem.IsWindows()) {
            return;
        }

        var self = ProcessHelpers.GetProcessInfo(Environment.ProcessId);
        await Assert.That(self).IsNotNull();

        var parent = ProcessHelpers.GetProcessInfo(self!.Value.ppid);

        await Assert.That(parent).IsNotNull();
        await Assert.That(ProcessHelpers.IsProcessAlive(self.Value.ppid)).IsTrue();
    }

    [Test]
    public async Task ParseExecPath_extracts_the_exec_path_from_a_procargs2_buffer() {
        // The exec path is the kernel's recorded image path and the only name source
        // immune to a process changing its title (Claude Code sets its title to its
        // version string, which clobbers proc_bsdinfo's name fields). Reading it is what
        // lets the agent ancestry walk match "claude" rather than "2.1.196".
        var buf = ProcArgs2.Of(2, "/Users/alexey/.local/bin/claude", "claude", "--resume");

        await Assert.That(ProcessHelpers.ParseExecPath(buf)).IsEqualTo("/Users/alexey/.local/bin/claude");
    }

    /// Gemini CLI runs as node with no title, so only its script names it: the shim the shebang hands over,
    /// or the entry point inside its package. Neither another runtime's script, an option's value nor inline
    /// code is taken for that script.
    [Test]
    [Arguments(new[] { "node", "/usr/local/bin/gemini", "--yolo" }, "gemini")]
    [Arguments(new[] { "/usr/bin/node", "--no-warnings", "/usr/lib/node_modules/@google/gemini-cli/dist/index.js" }, "gemini-cli")]
    [Arguments(new[] { "C:\\Program Files\\nodejs\\node.exe", "C:\\npm\\node_modules\\@google\\gemini-cli\\dist\\index.js" }, "gemini-cli")]
    [Arguments(new[] { "/usr/bin/python3", "/usr/local/bin/gemini" }, null)]
    [Arguments(new[] { "node" }, null)]
    [Arguments(new[] { "node", "--require", "/tmp/gemini.js", "/tmp/commit.js" }, null)]
    [Arguments(new[] { "node", "-e", "/usr/local/bin/gemini" }, null)]
    [Arguments(new[] { "node", "--no-warnings", "--", "/usr/local/bin/gemini" }, "gemini")]
    [Arguments(new[] { "bun", "run", "/usr/local/bin/gemini" }, "gemini")]
    [Arguments(new[] { "node", "--import", "tsx", "/usr/local/bin/gemini" }, "gemini")]
    [Arguments(new[] { "node", "/home/a/scripts/gemini.js" }, null)]
    [Arguments(new[] { "node", "--eval=1", "/usr/local/bin/gemini" }, null)]
    [Arguments(new[] { "node", "-pe", "/usr/local/bin/gemini" }, null)]
    public async Task A_script_run_by_node_names_its_agent(string[] argv, string? expected) {
        var names = ProcessArgs.ScriptNameCandidates(argv).ToList();

        await Assert.That(ProcessHelpers.ResolveCodingAgentPid(42, "gemini", pid => pid == 42 ? (1, names) : null))
            .IsEqualTo(expected is null ? null : 42);
    }

    [Test]
    public async Task SplitWindowsCommandLine_follows_the_runtime_quoting_rules() {
        await Assert.That(ProcessHelpers.SplitWindowsCommandLine(
                """
                "C:\Program Files\nodejs\node.exe" "C:\Users\a b\gemini.js" --yolo
                """))
            .IsEquivalentTo(["C:\\Program Files\\nodejs\\node.exe", "C:\\Users\\a b\\gemini.js", "--yolo"]);
        await Assert.That(ProcessHelpers.SplitWindowsCommandLine("""node a\\\"b c\\\\"d e" "ge""mini.js" """))
            .IsEquivalentTo(["node", "a\\\"b", "c\\\\d e", "ge\"mini.js"], CollectionOrdering.Matching);
        await Assert.That(ProcessHelpers.SplitWindowsCommandLine("\"C:\\node\\\" x.js"))
            .IsEquivalentTo(["C:\\node\\", "x.js"], CollectionOrdering.Matching);
        await Assert.That(ProcessHelpers.SplitWindowsCommandLine("C:\\\"Program Files\"\\node.exe a\u00a0b"))
            .IsEquivalentTo(["C:\\Program Files\\node.exe", "a\u00a0b"], CollectionOrdering.Matching);
        await Assert.That(ProcessHelpers.SplitWindowsCommandLine(null)).IsEmpty();
    }

    [Test]
    public async Task ParseExecPath_returns_null_for_a_buffer_too_short_for_a_path() {
        // Just the 4-byte argc with no path, and a buffer shorter than argc itself.
        await Assert.That(ProcessHelpers.ParseExecPath(new byte[] { 1, 0, 0, 0 })).IsNull();
        await Assert.That(ProcessHelpers.ParseExecPath(new byte[] { 0, 0 })).IsNull();
    }

    [Test]
    public async Task ParseExecPath_returns_null_when_the_exec_path_is_empty() {
        // argc followed immediately by the NUL terminator — no path present.
        await Assert.That(ProcessHelpers.ParseExecPath(ProcArgs2.Of(1, "", "claude"))).IsNull();
    }

    [Test]
    public async Task GetProcessInfo_reports_distinct_names_for_the_current_process() {
        // The three sources agree on an ordinary install, and the walk should see one entry
        // rather than the same string three times.
        var info = ProcessHelpers.GetProcessInfo(Environment.ProcessId);

        await Assert.That(info).IsNotNull();
        await Assert.That(info!.Value.names.Distinct(StringComparer.Ordinal).Count())
            .IsEqualTo(info.Value.names.Count);
    }
}
