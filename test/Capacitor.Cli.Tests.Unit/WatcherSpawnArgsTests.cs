using Capacitor.Cli.Core;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Tests.Unit;

public class WatcherSpawnArgsTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    /// <summary>A watcher spawned to follow another run carries that run's identity in place of the one it would
    /// inherit, an unknown run as empty values the watcher reads as unset; one spawned without leaves the inherited
    /// values alone.</summary>
    [Test]
    [Arguments("agent-2", "epoch-2")]
    [Arguments(null, null)]
    public async Task A_watcher_following_another_run_carries_its_identity(string? agentId, string? epoch) {
        var starter = FakeProcessStarter.Refusing();
        var spawner = new ProcessWatcherSpawner(
            Config.Root, Resolutions.At("http://localhost", Config.Root), WatcherPaths.FromEnvironment(Config.Root), starter, TimeProvider.System);

        await spawner.SpawnAsync(new WatcherSpawnRequest("abc", "/tmp/t.jsonl", null, Vendor: "muse") { RunsAs = new WatcherRun(agentId, epoch), Stderr = TextWriter.Null });
        await Assert.That(starter.Seen!.Environment[HostedAgent.AgentIdVar]).IsEqualTo(agentId ?? "");
        await Assert.That(starter.Seen!.Environment[HostedAgent.DaemonEpochVar]).IsEqualTo(epoch ?? "");

        await spawner.SpawnAsync(new WatcherSpawnRequest("abc", "/tmp/t.jsonl", null, Vendor: "muse") { Stderr = TextWriter.Null });
        await Assert.That(starter.Seen!.Environment.TryGetValue(HostedAgent.AgentIdVar, out var inherited) ? inherited : null)
            .IsEqualTo(Environment.GetEnvironmentVariable(HostedAgent.AgentIdVar));
    }

    /// <summary>The test host has no agent above it, which is what a hook sees when its agent cannot be named.
    /// A Copilot watcher then follows nothing, since all the fallback can find is a process whose exit says
    /// nothing about the session, a transient hook shell included.</summary>
    [Test]
    [Arguments("copilot", "/data/GitHub.copilot-chat/transcripts/abc.jsonl", false)]
    [Arguments("copilot", "/home/.copilot/session-state/abc/events.jsonl", false)]
    [Arguments("claude", "/home/.claude/projects/abc.jsonl", true)]
    public async Task Only_a_named_agent_gives_a_Copilot_watcher_a_parent(string vendor, string transcript, bool followsParent) {
        var starter = FakeProcessStarter.Refusing();
        var spawner = new ProcessWatcherSpawner(
            Config.Root, Resolutions.At("http://localhost", Config.Root), WatcherPaths.FromEnvironment(Config.Root), starter, TimeProvider.System);

        await spawner.SpawnAsync(new WatcherSpawnRequest("abc", transcript, null, Vendor: vendor) { Stderr = TextWriter.Null });

        await Assert.That(starter.Seen!.ArgumentList.Contains("--parent-pid")).IsEqualTo(followsParent);
    }

    /// <summary>Every spawn names its vendor, including the one `kcap watch` would have defaulted
    /// to: a reader of the command line never has to know what that default is.</summary>
    [Test]
    public async Task BuildSpawnArgs_names_the_default_vendor() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "claude"
        );

        await Assert.That(args.Take(3)).IsEquivalentTo(["watch", "abc", "/tmp/t.jsonl"], CollectionOrdering.Matching);
        await Assert.That(args.TakeLast(2)).IsEquivalentTo(["--vendor", "claude"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildSpawnArgs_codex_vendor_appends_flag() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "codex"
        );

        await Assert.That(args.TakeLast(2)).IsEquivalentTo(["--vendor", "codex"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildSpawnArgs_vendor_with_spaces_is_quoted() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: "/tmp/t.jsonl",
            agentId: null, sessionIdOverride: null,
            cwd: null, skipTitle: false, parentPid: null, vendor: "my vendor"
        );

        await Assert.That(args.TakeLast(2)).IsEquivalentTo(["--vendor", "my vendor"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task BuildSpawnArgs_with_agent_uses_session_override() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "sess-agent", transcriptPath: "/tmp/t.jsonl",
            agentId: "agent1", sessionIdOverride: "sess",
            cwd: "/repo", skipTitle: true, parentPid: 4242, vendor: "claude"
        );

        await Assert.That(args).IsEquivalentTo(
            ["watch", "sess", "/tmp/t.jsonl", "--agent-id", "agent1", "--cwd", "/repo", "--skip-title", "--parent-pid", "4242", "--vendor", "claude"],
            CollectionOrdering.Matching);
    }

    /// <summary>A Windows drive root ends in a backslash; as its own argument it cannot escape a quote and swallow the
    /// flags after it, so the watcher still learns its vendor.</summary>
    [Test]
    public async Task A_cwd_ending_in_a_backslash_leaves_the_flags_after_it_whole() {
        var args = ProcessWatcherSpawner.BuildSpawnArgs(
            key: "abc", transcriptPath: @"C:\r.jsonl", agentId: null, sessionIdOverride: null,
            cwd: @"C:\", skipTitle: true, parentPid: null, vendor: "codex"
        );

        await Assert.That(args).IsEquivalentTo(["watch", "abc", @"C:\r.jsonl", "--cwd", @"C:\", "--skip-title", "--vendor", "codex"], CollectionOrdering.Matching);
    }
}
