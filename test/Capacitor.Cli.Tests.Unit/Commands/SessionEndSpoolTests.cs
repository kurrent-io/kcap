using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// A session whose agent process is gone is ended on the server it was recorded against, through the
/// lifecycle spool, and left alone on any other.
public class SessionEndSpoolTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }

    static ExitedSession Exited(
            string id, string? vendor = "claude", string? server = "https://kcap.example", string? agentId = null, string? epoch = null) =>
        new(SessionId.Parse(id)!, new ClaimOrigin(vendor, server, "/t/x.jsonl", "/repo", agentId, epoch));

    static async Task<JsonNode?> SpooledEnd(HookSpool spool, string id) {
        string? body = null;
        await spool.DrainAllAsync(id, (_, b) => { body = b; return Task.FromResult(DrainOutcome.Delivered); }, TimeSpan.FromSeconds(5), CancellationToken.None);
        return body is null ? null : JsonNode.Parse(body);
    }

    /// <summary>A session that ended after a person disabled it has nothing left on the server to end, and the end would
    /// bring its deleted stream back.</summary>
    [Test]
    public async Task A_disabled_session_is_queued_no_end() {
        var spool = new HookSpool(Config.Root, TimeProvider.System, server: null);
        DisabledSessions.Mark("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", Config.Root);

        var queued = AgentHookPoster.SpoolEnds(
            [Exited("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"), Exited("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")], "https://kcap.example", spool, TimeProvider.System);

        await Assert.That(queued).IsEqualTo(1);
        await Assert.That(spool.HasBacklog("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")).IsFalse();
        await Assert.That(spool.HasBacklog("bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb")).IsTrue();
    }

    [Test]
    public async Task Only_a_session_recorded_against_this_server_is_queued() {
        var spool = new HookSpool(Config.Root, TimeProvider.System, server: null);

        var queued = AgentHookPoster.SpoolEnds(
            [Exited("here"), Exited("slash", server: "https://KCAP.example/"), Exited("elsewhere", server: "https://other.example"),
             Exited("novendor", vendor: null), Exited("noserver", server: null)],
            "https://kcap.example", spool, TimeProvider.System);

        await Assert.That(queued).IsEqualTo(2);
        await Assert.That(spool.HasBacklog("here")).IsTrue();
        await Assert.That(spool.HasBacklog("slash")).IsTrue();
        await Assert.That(spool.HasBacklog("elsewhere")).IsFalse();
        await Assert.That(spool.HasBacklog("novendor")).IsFalse();
        await Assert.That(spool.HasBacklog("noserver")).IsFalse();
    }

    [Test]
    public async Task The_end_is_a_parent_exited_session_end_for_the_vendor() {
        var spool = new HookSpool(Config.Root, TimeProvider.System, server: null);
        AgentHookPoster.SpoolEnds([Exited("here", vendor: "codex", agentId: "agent-1", epoch: "epoch-1")], "https://kcap.example", spool, TimeProvider.System);

        string? route = null, body = null;
        await spool.DrainAllAsync("here", (r, b) => { route = r; body = b; return Task.FromResult(DrainOutcome.Delivered); }, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(route).IsEqualTo("session-end/codex");
        var node = JsonNode.Parse(body!)!;
        await Assert.That(node["session_id"]!.GetValue<string>()).IsEqualTo("here");
        await Assert.That(node["transcript_path"]!.GetValue<string>()).IsEqualTo("/t/x.jsonl");
        await Assert.That(node["cwd"]!.GetValue<string>()).IsEqualTo("/repo");
        await Assert.That(node["hook_event_name"]!.GetValue<string>()).IsEqualTo("SessionEnd");
        await Assert.That(node["reason"]!.GetValue<string>()).IsEqualTo("parent_exited");
        await Assert.That(node["ended_at"]).IsNotNull();
        await Assert.That(node["agent_host_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(node["daemon_epoch"]!.GetValue<string>()).IsEqualTo("epoch-1");
    }

    /// The exit sweep racing a daemon resume, from the predecessor's claim note to the spooled end: the
    /// predecessor ran under epoch-1 and the restarted daemon, epoch-2, resumes it. Each sweep reaps the
    /// note the gone process left, so each phase below is where that one reap could land. The fence holds
    /// the end while the entry is Resuming and once it runs under epoch-2, but while the daemon holds the
    /// entry lock to write that transition it cannot decide and fails open on its lock timeout. The end it
    /// lets through names the agent and epoch the predecessor's claim recorded, which the server refuses it by.
    [Test]
    public async Task A_sweep_while_the_resuming_daemon_holds_the_entry_lock_posts_the_predecessors_end() {
        const string session = "2aa834d09c294410b64c177c75d32358";
        const int predecessor = 900_101;
        var ledger   = ResumeLedgerStore.For(Daemons.Store, "d1");
        var spool    = new HookSpool(Config.Root, TimeProvider.System, server: null);
        var sessions = new AgentSessions(Config.Root, _ => null);
        var entry    = ResumeLedgerEntries.For("agent-1", ResumeState.Running) with { LaunchEpoch = "epoch-1", RunEpoch = "epoch-1", SessionId = session };

        int Sweep() {
            var note = Config.Root.Path("agent-sessions", predecessor.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Directory.CreateDirectory(Path.GetDirectoryName(note)!);
            File.WriteAllText(note, $"{session}\nlx:another-boot:1");
            File.WriteAllText(note + ".origin",
                string.Join('\n', new[] { "lx:another-boot:1", "claude", "https://kcap.example", "/t/x.jsonl", "/repo", "agent-1", "epoch-1" }.Select(Uri.EscapeDataString)));

            return AgentHookPoster.SpoolEnds(sessions.ReapExited("https://kcap.example"), "https://kcap.example", spool, TimeProvider.System,
                                             s => SessionEndFence.ShouldHold(s, Daemons.Store, TimeProvider.System));
        }

        ledger.Write(entry with { State = ResumeState.Resuming });
        await Assert.That(Sweep()).IsEqualTo(0);

        var resumed = entry with { RunEpoch = "epoch-2" };
        using (ResumeEntryLock.TryAcquire(ledger.EntryPath("agent-1"), TimeSpan.FromSeconds(1))) {
            ledger.Write(resumed);
            await Assert.That(ledger.TryUpdateUnderLock("agent-1", _ => null, TimeSpan.FromMilliseconds(20))).IsEqualTo(LockedUpdate.LockTimeout);
            await Assert.That(Sweep()).IsEqualTo(1);
        }

        var posted = (await SpooledEnd(spool, session))!;
        await Assert.That(posted["reason"]!.GetValue<string>()).IsEqualTo("parent_exited");
        await Assert.That(posted["agent_host_id"]!.GetValue<string>()).IsEqualTo("agent-1");
        await Assert.That(posted["daemon_epoch"]!.GetValue<string>()).IsEqualTo("epoch-1");

        await Assert.That(Sweep()).IsEqualTo(0);
    }

    [Test]
    public async Task Nothing_is_queued_without_a_server_to_post_to() =>
        await Assert.That(AgentHookPoster.SpoolEnds([Exited("here")], null, new HookSpool(Config.Root, TimeProvider.System, server: null), TimeProvider.System))
            .IsEqualTo(0);

    [Test]
    public async Task A_session_whose_end_the_fence_holds_is_not_queued() {
        var spool  = new HookSpool(Config.Root, TimeProvider.System, server: null);
        var origin = new ClaimOrigin("claude", "https://kcap.example", "/t/x.jsonl", "/repo", AgentId: "agent-1", DaemonEpoch: "epoch-1");

        var queued = AgentHookPoster.SpoolEnds(
            [new ExitedSession(SessionId.Parse("held")!, origin), Exited("here")], "https://kcap.example", spool, TimeProvider.System,
            hold: exited => exited.Origin?.AgentId == "agent-1");

        await Assert.That(queued).IsEqualTo(1);
        await Assert.That(spool.HasBacklog("held")).IsFalse();
        await Assert.That(spool.HasBacklog("here")).IsTrue();
    }
}
