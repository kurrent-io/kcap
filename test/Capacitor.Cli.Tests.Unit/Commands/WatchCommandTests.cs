using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class RepoPayloadChangedTests {
    static RepositoryPayload MakePayload(
            string owner    = "o",
            string repo     = "r",
            string branch   = "main",
            int?   prNumber = 1,
            string prUrl    = "u",
            string prTitle  = "t"
        ) => new() { Owner = owner, RepoName = repo, Branch = branch, PrNumber = prNumber, PrUrl = prUrl, PrTitle = prTitle };

    [Test]
    public async Task NullCurrent_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, MakePayload())).IsFalse();

    [Test]
    public async Task NullLastSent_ReturnsTrue() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), null)).IsTrue();

    [Test]
    public async Task BothNull_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(null, null)).IsFalse();

    [Test]
    public async Task SameValues_ReturnsFalse() =>
        await Assert.That(WatchCommand.RepoPayloadChanged(MakePayload(), MakePayload())).IsFalse();

    [Test]
    [Arguments("Owner")]
    [Arguments("RepoName")]
    [Arguments("Branch")]
    [Arguments("PrNumber")]
    [Arguments("PrUrl")]
    [Arguments("PrTitle")]
    public async Task DifferentField_ReturnsTrue(string field) {
        var a = MakePayload();

        var b = field switch {
            "Owner"    => a with { Owner = "x" },
            "RepoName" => a with { RepoName = "x" },
            "Branch"   => a with { Branch = "x" },
            "PrNumber" => a with { PrNumber = 99 },
            "PrUrl"    => a with { PrUrl = "x" },
            "PrTitle"  => a with { PrTitle = "x" },
            _          => a
        };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsTrue();
    }

    [Test]
    public async Task NonComparedFields_DoNotTriggerChange() {
        var a = MakePayload() with { UserName = "alice" };
        var b = MakePayload() with { UserName = "bob" };
        await Assert.That(WatchCommand.RepoPayloadChanged(a, b)).IsFalse();
    }
}

public class CountFileLinesTests {
    [Test]
    [Arguments("line1\nline2\nline3\n", 3)]
    [Arguments("single", 1)]
    [Arguments("", 0)]
    public async Task CountsLines(string content, int expected) {
        using var tmp  = new TempDir();
        var       path = tmp.CreateFile("lines.tmp", content);
        await Assert.That(WatchCommand.CountFileLines(path)).IsEqualTo(expected);
    }

    [Test]
    public async Task MissingFile_ReturnsZero() =>
        await Assert.That(WatchCommand.CountFileLines("/tmp/nonexistent_" + Guid.NewGuid())).IsEqualTo(0);
}

public class WatchCommandTests {
    /// <summary>A successor watcher can carry another run's identity, so a handed end is judged only as the run its hook
    /// named: against a ledger running epoch-2, the live run's own end goes through. An end that names no run is placed
    /// by its session, as an end with no epoch, so after a resume it is held rather than posted as the live run's.</summary>
    [Test]
    public async Task A_handed_end_is_judged_as_the_run_its_hook_named_or_else_by_its_session() {
        const string session = "2aa834d09c294410b64c177c75d32358";
        using var daemons = new TempDaemonStore();
        Capacitor.Cli.Core.Resume.ResumeLedgerStore.For(daemons.Store, "d1").Write(
            ResumeLedgerEntries.For("agent-1", Capacitor.Cli.Core.Resume.ResumeState.Running) with {
                LaunchEpoch = "epoch-1", RunEpoch = "epoch-2", SessionId = session
            });
        string? Held(string payload) => WatchCommand.HandedEndHeld(payload, session,
            run => SessionEndFence.ShouldHold(run.AgentId, run.DaemonEpoch, session, id => Capacitor.Cli.Core.Resume.ResumeLedgerStore.LocateByAgentId(daemons.Store, id), TimeProvider.System),
            exited => SessionEndFence.ShouldHold(exited, daemons.Store, TimeProvider.System));

        await Assert.That(Held("""{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-2"}""")).IsNull();
        await Assert.That(Held("""{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-1"}""")).IsEqualTo("agent-1");
        await Assert.That(Held("""{"reason":"exit"}""")).IsEqualTo(session);
    }

    /// <summary>A named handed end the fence lets through stamps the run's ledger entry with the session it ends, so the
    /// daemon can tell it from the end of an earlier session of the same process.</summary>
    [Test, NotInParallel]
    public async Task A_named_handed_end_let_through_commits_its_own_session() {
        const string session = "2aa834d09c294410b64c177c75d32358";
        using var daemons = new TempDaemonStore();
        var ledger = Capacitor.Cli.Core.Resume.ResumeLedgerStore.For(daemons.Store, "d1");
        ledger.Write(ResumeLedgerEntries.For("agent-1", Capacitor.Cli.Core.Resume.ResumeState.Running) with {
            LaunchEpoch = "epoch-2", RunEpoch = "epoch-2", SessionId = session
        });
        using var dir = EnvScope.Exclusive("KCAP_DAEMONS_DIR", daemons.Directory);
        var handed = new Capacitor.Cli.Core.Harness.Muse.MuseSessionEndRequest(
            """{"reason":"exit","agent_host_id":"agent-1","daemon_epoch":"epoch-2"}""", "claimed", DateTimeOffset.UtcNow, "source");

        await Assert.That(WatchCommand.AgentHoldingEndForResume(handed, session)).IsNull();
        await Assert.That(ledger.TryRead("agent-1")!.EndCommittedSession).IsEqualTo(session);
    }

    /// <summary>A successor watcher takes the run the daemon's marker records only when the marker names the process it
    /// follows; a marker of another process, one an older daemon wrote, or none at all gives it no run.</summary>
    [Test]
    public async Task A_successor_watcher_follows_the_run_the_marker_records_for_its_process() {
        using var home = new TempHome();
        const string session = "2aa834d09c294410b64c177c75d32358";
        var followed = Environment.ProcessId;
        var none     = new WatcherRun(null, null);
        var marker   = new Capacitor.Cli.Core.Harness.Muse.MuseHostedSessionMarker(session, "agent-1", "daemon-1", "epoch-2", DateTimeOffset.UtcNow,
            OwnerToken: ProcessStartToken.ForCurrent(), OwnerPid: followed);

        await Assert.That(WatchCommand.SuccessorRun(home, session, followed)).IsEqualTo(none);

        Capacitor.Cli.Core.Harness.Muse.MuseHostedSessionMarker.Write(home, marker);
        await Assert.That(WatchCommand.SuccessorRun(home, session, followed)).IsEqualTo(new WatcherRun("agent-1", "epoch-2"));
        await Assert.That(WatchCommand.SuccessorRun(home, session, followed + 1)).IsEqualTo(none);

        Capacitor.Cli.Core.Harness.Muse.MuseHostedSessionMarker.Write(home, marker with { OwnerToken = null, OwnerPid = null });
        await Assert.That(WatchCommand.SuccessorRun(home, session, followed)).IsEqualTo(none);
    }

    /// <summary>A handed end carries only the run identity its hook named; one that names none, an unparseable one
    /// included, carries none, since the watcher's own identity can be another run's.</summary>
    [Test]
    public async Task A_handed_end_names_only_the_run_its_hook_named() {
        var at = new DateTimeOffset(2026, 11, 2, 18, 26, 34, TimeSpan.Zero);

        var unnamed = WatchCommand.HandedEndBody("""{"reason":"exit"}""", "s1", "/t/x.jsonl", at, out _);
        var named   = WatchCommand.HandedEndBody("""{"reason":"exit","agent_host_id":"agent-hook","daemon_epoch":"epoch-hook"}""", "s1", "/t/x.jsonl", at, out _);
        var garbled = WatchCommand.HandedEndBody("{not json", "s1", "/t/x.jsonl", at, out var notJson);

        await Assert.That((named["agent_host_id"]?.GetValue<string>(), named["daemon_epoch"]?.GetValue<string>())).IsEqualTo(("agent-hook", "epoch-hook"));
        await Assert.That(unnamed.ContainsKey("agent_host_id") || unnamed.ContainsKey("daemon_epoch")).IsFalse();
        await Assert.That(garbled.ContainsKey("agent_host_id") || garbled.ContainsKey("daemon_epoch")).IsFalse();
        await Assert.That(notJson).IsNotNull();
        await Assert.That(unnamed["session_id"]?.GetValue<string>()).IsEqualTo("s1");
    }

    /// <summary>A hosted watcher's own end names the agent and daemon epoch it runs under, so a spooled copy replayed
    /// after the session resumes reads as the predecessor's; an unhosted watcher's end names neither.</summary>
    [Test]
    public async Task The_watchers_own_end_names_the_hosted_agent_and_daemon_epoch_it_runs_under() {
        var at     = new DateTimeOffset(2026, 11, 2, 18, 26, 34, TimeSpan.Zero);
        var hosted = WatchCommand.ParentExitEndBody("s1", "/t/x.jsonl", "/repo", "parent_exited", at,
            new HostedAgent("agent-1", false, DaemonBridge.None) { DaemonEpoch = "epoch-1" });
        var plain  = WatchCommand.ParentExitEndBody("s1", "/t/x.jsonl", "/repo", "parent_exited", at, HostedAgent.Terminal);

        await Assert.That(hosted["agent_host_id"]?.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(hosted["daemon_epoch"]?.GetValue<string>()).IsEqualTo("epoch-1");
        await Assert.That(hosted["reason"]?.GetValue<string>()).IsEqualTo("parent_exited");
        await Assert.That(hosted["ended_at"]?.GetValue<string>()).IsEqualTo(at.ToString("O"));
        await Assert.That(plain.ContainsKey("agent_host_id")).IsFalse();
        await Assert.That(plain.ContainsKey("daemon_epoch")).IsFalse();
    }

    [Test]
    public async Task RunWatch_signature_accepts_vendor_arg() {
        // We can't run a real watcher in a unit test (it'd open SignalR). The
        // hook round-trip integration test exercises the wire path; this guards
        // the signature.
        var method      = typeof(WatchCommand).GetMethod(nameof(WatchCommand.RunWatch))!;
        var vendorParam = method.GetParameters().FirstOrDefault(p => p.Name == "vendor");
        await Assert.That(vendorParam).IsNotNull();
        await Assert.That(vendorParam!.HasDefaultValue).IsTrue();
        await Assert.That(vendorParam.DefaultValue).IsEqualTo("claude");
    }

    static readonly TimeSpan RecoveryCeiling = TimeSpan.FromHours(6);

    [Test]
    public async Task DecideParentDeadRecovery_reArms_when_parent_reResolved_alive() {
        // Preferred outcome: re-resolution found the parent and it's alive → re-arm, end nothing —
        // even if the no-progress window already exceeds the ceiling.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: 4321, isAlive: _ => true,
            noProgressElapsed: RecoveryCeiling + TimeSpan.FromMinutes(1), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.ReArm);
    }

    [Test]
    public async Task DecideParentDeadRecovery_endsTerminal_when_resolution_fails_past_ceiling() {
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: null, isAlive: _ => false,
            noProgressElapsed: RecoveryCeiling + TimeSpan.FromMinutes(1), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.EndTerminal);
    }

    [Test]
    public async Task DecideParentDeadRecovery_keepsWaiting_below_ceiling_with_no_parent() {
        // A live-but-idle Kiro/OpenCode user parked at a prompt: no parent resolved, but under the
        // ceiling → keep waiting, do NOT end.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: null, isAlive: _ => false,
            noProgressElapsed: TimeSpan.FromHours(1), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.KeepWaiting);
    }

    [Test]
    [Arguments(1)]
    [Arguments(0)]
    [Arguments(-1)]
    public async Task DecideParentDeadRecovery_keepsWaiting_on_an_implausible_pid(int reResolved) {
        // Re-arming on init (or anything below it) watchdogs an immortal PID, so the agent's
        // death can never be observed and only the idle ceiling can end the session.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolved, _ => true, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(90)
        );

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.KeepWaiting);
    }

    [Test]
    public async Task DecideParentDeadRecovery_keepsWaiting_when_reResolved_pid_is_dead() {
        // Re-resolution returned a transient/dead pid → not a valid re-arm target; below ceiling.
        var decision = WatchCommand.DecideParentDeadRecovery(
            reResolvedPid: 9, isAlive: _ => false,
            noProgressElapsed: TimeSpan.FromMinutes(5), ceiling: RecoveryCeiling);

        await Assert.That(decision).IsEqualTo(WatchCommand.ParentDeadRecovery.KeepWaiting);
    }

    [Test]
    [Arguments(null, 360)]   // unset → default 6h
    [Arguments("", 360)]     // empty → default
    [Arguments("abc", 360)]  // non-numeric → default
    [Arguments("0", 360)]    // non-positive → default
    [Arguments("-5", 360)]   // negative → default
    [Arguments("120", 120)]  // valid override
    public async Task ResolveParentDeadCeiling_parses_env_with_default(string? env, int expectedMinutes) {
        var result = WatchCommand.ResolveParentDeadCeiling(env);

        await Assert.That(result).IsEqualTo(TimeSpan.FromMinutes(expectedMinutes));
    }

    // #550 companion: with children joining the reap ceiling, ScanCodexSubagents' first-sight-only
    // gate became a content-loss hazard — a ceiling-reaped child whose subagent re-engages would
    // grow its rollout with nobody watching for the rest of the parent's life. The scan therefore
    // re-ensures the child watcher whenever the rollout's mtime advances; this pure gate decides
    // when, recording the mtime it fired for so an unchanged rollout stays quiet.
    [Test]
    public async Task ChildRolloutAdvanced_fires_on_first_sight_and_then_only_on_mtime_change() {
        var mtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var t0     = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/a.jsonl", t0)).IsTrue();
        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/a.jsonl", t0)).IsFalse();
        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/a.jsonl", t0.AddSeconds(1))).IsTrue();
        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/a.jsonl", t0.AddSeconds(1))).IsFalse();
    }

    // Distinct rollouts are tracked independently — one child's growth must not re-ensure another.
    [Test]
    public async Task ChildRolloutAdvanced_tracks_each_rollout_independently() {
        var mtimes = new Dictionary<string, DateTime>(StringComparer.Ordinal);
        var t0     = new DateTime(2026, 8, 13, 12, 0, 0, DateTimeKind.Utc);

        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/a.jsonl", t0)).IsTrue();
        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/b.jsonl", t0)).IsTrue();
        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/a.jsonl", t0.AddSeconds(5))).IsTrue();
        await Assert.That(WatchCommand.ChildRolloutAdvanced(mtimes, "/b.jsonl", t0)).IsFalse();
    }

    [Test]
    public async Task Cursor_acked_ack_sets_next_line_cursor() {
        var ack = System.Text.Json.JsonSerializer.Deserialize(
            """{"next_line_number":7}""", CapacitorJsonContext.Default.TranscriptBatchAck);

        await Assert.That(ack.NextLineNumber).IsEqualTo(7);
    }

    // Cursor's own sessionStart hook posts (and spawns this very watcher)
    // before any transcript line is ever read, exactly like Antigravity — so the generic
    // below-threshold buffer must not apply to it either.
    [Test]
    [Arguments("antigravity", true)]
    [Arguments("cursor",      true)]
    [Arguments("mistral-vibe", true)]
    [Arguments("grok",        true)]
    [Arguments("muse",        true)]
    [Arguments("codex",       false)]
    [Arguments("claude",      false)]
    [Arguments("gemini",      false)]
    public async Task SkipsThresholdBuffering_matches_only_the_vendors_that_pre_commit_the_session(string vendor, bool expected) =>
        await Assert.That(WatchCommand.SkipsThresholdBuffering(vendor)).IsEqualTo(expected);
}

// Claude's in-flight guard for the subagent idle ceiling. Shapes below are taken verbatim from a
// real subagent transcript: tool_use carries `id`, tool_result carries `tool_use_id`, both inside
// message.content[].
// Loss markers carry no correlation IDs, so local trackers must use raw source lines.
// PR #162: Pi emits type:"message" with message.role (not Claude's
// top-level user/assistant), so the watcher title helpers need a Pi branch —
// otherwise live Pi sessions never get the initial/LLM title.
