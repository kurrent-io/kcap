using System.Text.Json.Nodes;
using TUnit.Assertions.Enums;

namespace Capacitor.Cli.Core.Tests.Unit;

public class HookSpoolTests {
    const string SidA = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    const string SidB = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    const string SidC = "cccccccccccccccccccccccccccccccc";

    /// <summary>The one classification every live post and drain poster shares. A 401 is retryable
    /// because <c>kcap login</c> repairs it; a 4xx that rejects the payload itself is not, since a
    /// replay would be rejected the same way. Neither is the 410 a session its owner deleted on the
    /// server answers: the session is gone for good.</summary>
    [Test]
    [Arguments(401, true)]
    [Arguments(408, true)]
    [Arguments(429, true)]
    [Arguments(500, true)]
    [Arguments(503, true)]
    [Arguments(400, false)]
    [Arguments(403, false)]
    [Arguments(404, false)]
    [Arguments(410, false)]
    [Arguments(422, false)]
    public async Task retryable_statuses_stop_the_drain_and_the_rest_drop(int status, bool retryable) {
        await Assert.That(HookSpool.IsRetryable(status)).IsEqualTo(retryable);
        await Assert.That(HookSpool.OutcomeOf(status)).IsEqualTo(retryable ? DrainOutcome.TransientStop : DrainOutcome.Drop);
    }

    /// <summary>This drain runs on every Claude hook of every session, so it usually gets to a disabled session's entries
    /// ahead of the ordered one.</summary>
    [Test]
    public async Task the_fifo_drain_posts_nothing_of_a_disabled_session_and_discards_it() {
        using var config = new TempConfigRoot();
        var spool = new HookSpool(config.Root, TimeProvider.System, "http://s");
        spool.Append(SidA, "session-start", """{"n":"disabled"}""");
        spool.Append(SidB, "session-start", """{"n":"other"}""");
        DisabledSessions.Mark(SidA, config.Root);

        var posted = new List<string>();
        await spool.DrainAllAsync(null, (_, body) => { posted.Add(body); return Task.FromResult(DrainOutcome.Delivered); }, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(posted).IsEquivalentTo(new[] { """{"n":"other"}""" });
        await Assert.That(spool.HasBacklog(SidA)).IsFalse();
    }

    /// <summary>This drain delivers only its own temps, so what the ordered drain left of the session goes on entry.</summary>
    [Test]
    public async Task the_fifo_drain_discards_what_the_ordered_drain_left_of_a_disabled_session() {
        using var config = new TempConfigRoot();
        var spool = new HookSpool(config.Root, TimeProvider.System, "http://s");
        spool.Append(SidA, "session-start", """{"n":1}""");
        var ordered = Path.Combine(spool.DirectoryPath, $"{SidA}.ordered-1-1");
        await File.WriteAllTextAsync(ordered, "{\"route\":\"session-end\",\"body\":{}}\n");
        DisabledSessions.Mark(SidA, config.Root);

        await spool.DrainAllAsync(null, (_, _) => Task.FromResult(DrainOutcome.Delivered), TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(File.Exists(ordered)).IsFalse();
    }

    [Test]
    public async Task the_fifo_drain_posts_no_more_of_a_session_disabled_during_its_pass() {
        using var config = new TempConfigRoot();
        var spool = new HookSpool(config.Root, TimeProvider.System, "http://s");
        spool.Append(SidA, "session-start", """{"n":1}""");
        spool.Append(SidA, "session-end", """{"n":2}""");

        var posted = new List<string>();
        await spool.DrainAllAsync(null, (_, body) => {
            posted.Add(body);
            DisabledSessions.Mark(SidA, config.Root);

            return Task.FromResult(DrainOutcome.Delivered);
        }, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(posted).IsEquivalentTo(new[] { """{"n":1}""" });
        await Assert.That(spool.HasBacklog(SidA)).IsFalse();
    }

    [Test]
    public async Task the_fifo_drain_delivers_an_excluded_sessions_entries() {
        using var config = new TempConfigRoot();
        var spool = new HookSpool(config.Root, TimeProvider.System, "http://s");
        spool.Append(SidA, "session-start", """{"n":1}""");
        DisabledSessions.Exclude(SidA, config.Root);

        var posted = new List<string>();
        await spool.DrainAllAsync(null, (_, body) => { posted.Add(body); return Task.FromResult(DrainOutcome.Delivered); }, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(posted).Contains("""{"n":1}""");
    }

    /// <summary>Either drain sends only what was spooled for its own server, from either spool and
    /// however that server's URL is spelled. What an older build left unpartitioned in the root goes
    /// to whichever server drains first.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_drain_sends_only_what_was_spooled_for_its_own_server(bool ordered) {
        using var config = new TempConfigRoot();
        const string local = "http://localhost:7248";
        new HookSpool(config.Root, TimeProvider.System, local).Append(SidA, "session-start", """{"n":"local"}""");
        new TranscriptSpool(config.Root, TimeProvider.System, local).Append(SidC, """{"n":"local-tail"}""");
        new HookSpool(config.Root.Path(HookSpool.DirectoryName), TimeProvider.System).Append(SidB, "session-start", """{"n":"legacy"}""");

        async Task<List<string>> DrainAs(string server) {
            var seen = new List<string>();
            Task<DrainOutcome> Post(string body) { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); }
            var spool = new HookSpool(config.Root, TimeProvider.System, server);
            if (ordered)
                await LifecycleSpoolDrain.RunAsync(config.Root, spool, new TranscriptSpool(config.Root, TimeProvider.System, server),
                    currentSessionId: null, (_, body) => Post(body), Post, TimeSpan.FromSeconds(5), TimeProvider.System, CancellationToken.None);
            else
                await spool.DrainAllAsync(null, (_, body) => Post(body), TimeSpan.FromSeconds(5), CancellationToken.None);
            return seen;
        }

        await Assert.That(await DrainAs("https://kurrent.kcap.ai")).IsEquivalentTo(["""{"n":"legacy"}"""]);
        await Assert.That(await DrainAs("http://LOCALHOST:7248/")).Contains("""{"n":"local"}""");
    }

    /// <summary>A session spooled both before and after the upgrade has a file of the same name in the root
    /// and in the partition. The route-agnostic drain must still deliver both, the older one first.</summary>
    [Test]
    public async Task a_colliding_unpartitioned_file_drains_before_the_partition_one() {
        using var config = new TempConfigRoot();
        const string server = "https://kurrent.kcap.ai";
        new HookSpool(config.Root.Path(HookSpool.DirectoryName), TimeProvider.System).Append(SidA, "session-start", """{"n":"legacy"}""");
        var spool = new HookSpool(config.Root, TimeProvider.System, server);
        spool.Append(SidA, "user-prompt", """{"n":"partitioned"}""");

        var seen = new List<string>();
        await spool.DrainAllAsync(null, (_, body) => { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(seen).IsEquivalentTo(["""{"n":"legacy"}""", """{"n":"partitioned"}"""], CollectionOrdering.Matching);
    }

    [Test]
    public async Task drains_current_session_first_then_others_in_fifo() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidB, "session-start", """{"n":"b1"}""");
        spool.Append(SidA, "session-start", """{"n":"a1"}""");
        spool.Append(SidA, "session-end",   """{"n":"a2"}""");

        var seen = new List<string>();
        await spool.DrainAllAsync(SidA, (route, body) => {
            seen.Add($"{route}:{body}");
            return Task.FromResult(DrainOutcome.Delivered);
        }, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Current session A first (FIFO a1, a2), then B.
        await Assert.That(seen).IsEquivalentTo([
            """session-start:{"n":"a1"}""",
            """session-end:{"n":"a2"}""",
            """session-start:{"n":"b1"}""",
        ]);
        await Assert.That(spool.HasBacklog(SidA)).IsFalse();
        await Assert.That(spool.HasBacklog(SidB)).IsFalse();
    }

    [Test]
    public async Task transient_stop_keeps_remainder_drop_advances() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidA, "session-start", """{"n":1}"""); // Delivered
        spool.Append(SidA, "session-start", """{"n":2}"""); // Drop (permanent)
        spool.Append(SidA, "session-end",   """{"n":3}"""); // TransientStop

        await spool.DrainAllAsync(SidA, (route, body) =>
            Task.FromResult(body.Contains('2') ? DrainOutcome.Drop
                          : body.Contains('3') ? DrainOutcome.TransientStop
                          : DrainOutcome.Delivered),
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // n1 delivered, n2 dropped, n3 left for next time. After a partial drain the
        // remainder lives in a .draining temp; read whatever files remain in the spool dir.
        var all = string.Concat(Directory.EnumerateFiles(tmp.Path).Select(File.ReadAllText));
        // body values may be Unicode-escaped (" for "), so check the body field content directly
        await Assert.That(all).Contains("session-end"); // n3 route still present
        await Assert.That(all).DoesNotContain("session-start"); // n1 (delivered) and n2 (dropped) gone
    }

    [Test]
    public async Task concurrent_append_during_drain_is_not_lost() {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidA, "session-start", """{"n":"old"}""");

        // Poster appends a NEW entry while the OLD one is being drained (live file
        // already rotated to a temp), simulating a racing hook on the same session.
        var appended = false;
        var seen     = new List<string>();
        await spool.DrainAllAsync(SidA, (route, body) => {
            seen.Add(body);
            if (!appended) { spool.Append(SidA, "session-end", """{"n":"new"}"""); appended = true; }
            return Task.FromResult(DrainOutcome.Delivered);
        }, TimeSpan.FromSeconds(5), CancellationToken.None);

        // Delivered in the same pass, or left in a fresh live file for the next one.
        var all = string.Concat(seen) + string.Concat(Directory.EnumerateFiles(tmp.Path).Select(File.ReadAllText));
        await Assert.That(all).Contains("new");
    }

    [Test]
    public async Task old_format_lines_without_route_are_skipped() {
        using var tmp = new TempDir();
        tmp.CreateFile($"{SidA}.jsonl",
            "{\"hook_event_name\":\"sessionEnd\",\"body\":\"x\"}\n");
        var count = 0;
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        await spool.DrainAllAsync(SidA, (_, _) => { count++; return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);
        await Assert.That(count).IsEqualTo(0); // skipped, not posted
    }

    [Test]
    public async Task recovered_draining_temp_drains_before_live_file() {
        using var tmp = new TempDir();
        // Simulate a crash mid-drain: an older .draining temp + a newer live file.
        tmp.CreateFile($"{SidA}.123-1.draining",
            "{\"route\":\"session-start\",\"body\":\"old\"}\n");
        await Task.Delay(10);
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidA, "session-end", """{"n":"newlive"}""");

        var seen = new List<string>();
        await spool.DrainAllAsync(SidA, (route, body) => { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(seen[0]).IsEqualTo("old"); // temp first
        await Assert.That(seen).Contains("""{"n":"newlive"}""");
    }

    // Task 12 / BLOCKER-1: the ordered drain (LifecycleSpoolDrain / DrainRoutesAsync) uses a
    // distinct ".ordered-*" temp namespace precisely so it can deliberately WITHHOLD a phase's
    // remainder mid-pass (e.g. a session-end held back until the transcript tail is done) without
    // the unrelated route-agnostic FIFO drain (DrainAllAsync, still used by Claude/Cursor) sweeping
    // it up and delivering it immediately via the wrong poster — reintroducing the exact ordering
    // race the two-phase drain exists to prevent.
    [Test]
    public async Task DrainAllAsync_never_recovers_an_ordered_drain_temp() {
        using var tmp = new TempDir();
        // A withheld ordered-drain remainder — as DrainRoutesAsync would leave it — sitting
        // right alongside a fresh route-agnostic append for the SAME session. A start would end
        // the withheld end's run, so the fresh append is not one.
        tmp.CreateFile($"{SidA}.ordered-123-1",
            "{\"route\":\"session-end\",\"body\":\"withheld\"}\n");
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidA, "subagent-stop", """{"n":"fresh"}""");

        var seen = new List<string>();
        await spool.DrainAllAsync(SidA, (route, body) => { seen.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            TimeSpan.FromSeconds(5), CancellationToken.None);

        // Only the fresh, route-agnostic entry was delivered — the ordered-drain temp was never
        // touched (still on disk, byte-for-byte).
        await Assert.That(seen).IsEquivalentTo(["""{"n":"fresh"}"""]);
        await Assert.That(File.Exists(tmp.PathTo($"{SidA}.ordered-123-1"))).IsTrue();
        await Assert.That(await File.ReadAllTextAsync(tmp.PathTo($"{SidA}.ordered-123-1")))
            .IsEqualTo("{\"route\":\"session-end\",\"body\":\"withheld\"}\n");
    }

    /// <summary>A session-start an older build spooled to the root is still undelivered until a drain
    /// adopts it, so a later session-end posted directly would reach the server first.</summary>
    [Test]
    public async Task HasBacklog_sees_an_unpartitioned_file_no_drain_has_adopted_yet() {
        using var config = new TempConfigRoot();
        new HookSpool(config.Root.Path(HookSpool.DirectoryName), TimeProvider.System).Append(SidA, "session-start", "{}");
        new TranscriptSpool(config.Root.Path(TranscriptSpool.DirectoryName), TimeProvider.System).Append(SidA, "{}");

        await Assert.That(new HookSpool(config.Root, TimeProvider.System, "https://kurrent.kcap.ai").HasBacklog(SidA)).IsTrue();
        await Assert.That(new TranscriptSpool(config.Root, TimeProvider.System, "https://kurrent.kcap.ai").HasBacklog(SidA)).IsTrue();
    }

    [Test]
    public async Task HasBacklog_sees_an_ordered_drain_temp_even_though_DrainAllAsync_ignores_it() {
        using var tmp = new TempDir();
        tmp.CreateFile($"{SidA}.ordered-1-1",
            "{\"route\":\"session-end\",\"body\":\"x\"}\n");
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);

        // Ordering guards (ClaudeHookCommand.CurrentSessionHasBacklog / CursorHookCommand) must
        // see this as backlog so they defer their OWN fresh post rather than race ahead of a
        // withheld ordered-drain phase still in flight for this session.
        await Assert.That(spool.HasBacklog(SidA)).IsTrue();
    }

    [Test]
    public async Task reap_deletes_stale_files() {
        using var tmp = new TempDir();
        var f = tmp.PathTo($"{SidA}.jsonl");
        await File.WriteAllTextAsync(f, "{\"route\":\"x\",\"body\":\"y\"}\n");
        File.SetLastWriteTimeUtc(f, DateTime.UtcNow.AddDays(-40));
        new HookSpool(tmp.Path, time: TimeProvider.System).DeleteExpired();
        await Assert.That(File.Exists(f)).IsFalse();
    }

    [Test]
    public async Task cap_holds_in_bytes_for_non_ascii_payloads() {
        using var tmp = new TempDir();
        // Multi-byte UTF-8 bodies: char-count under-counts bytes, so a char-based cap would
        // let the file grow past capBytes. With byte-based counting the file stays bounded.
        var spool = new HookSpool(tmp.Path, capBytes: 400, time: TimeProvider.System);
        for (var i = 0; i < 30; i++)
            spool.Append(SidA, "session-end", $$"""{"i":{{i}},"t":"日本語テキスト😀"}""");

        var path  = tmp.PathTo($"{SidA}.jsonl");
        var bytes = new FileInfo(path).Length;
        await Assert.That(bytes).IsLessThanOrEqualTo(400L);
        // Eviction happened (FIFO): newest entry retained, oldest dropped.
        var ids = (await File.ReadAllLinesAsync(path))
            .Select(l => JsonNode.Parse(l)!["body"]!.GetValue<string>())
            .Select(b => JsonNode.Parse(b)!["i"]!.GetValue<int>())
            .ToList();
        await Assert.That(ids.Count).IsLessThan(30);
        await Assert.That(ids).Contains(29);
        await Assert.That(ids).DoesNotContain(0);
    }

    const string SnapshotUpload = """{"session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","snapshot_id":"abc123","documents":[]}""";

    /// <summary>The judge stops sending a snapshot inline once any drain has delivered it — the
    /// route-agnostic one and the ordered one alike, whichever vendor's hook ran it.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_delivered_policy_snapshot_is_recorded_by_either_drain(bool ordered) {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidA, HookSpool.PolicySnapshotRoute, SnapshotUpload);
        static Task<DrainOutcome> Delivered(string route, string body) => Task.FromResult(DrainOutcome.Delivered);

        if (ordered) await spool.DrainRoutesAsync(SidA, isTerminal: false, Delivered, () => false, CancellationToken.None);
        else await spool.DrainAllAsync(SidA, Delivered, TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(spool.IsPolicySnapshotDelivered(SidA, "abc123")).IsTrue();
        await Assert.That(spool.SessionIdsWithBacklog()).IsEmpty();
    }

    [Test]
    [Arguments(DrainOutcome.TransientStop)]
    [Arguments(DrainOutcome.Drop)]
    public async Task an_undelivered_policy_snapshot_is_not_recorded(DrainOutcome outcome) {
        using var tmp = new TempDir();
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(SidA, HookSpool.PolicySnapshotRoute, SnapshotUpload);

        await spool.DrainAllAsync(SidA, (_, _) => Task.FromResult(outcome), TimeSpan.FromSeconds(5), CancellationToken.None);

        await Assert.That(spool.IsPolicySnapshotDelivered(SidA, "abc123")).IsFalse();
    }

    [Test]
    public async Task A_discard_leaves_the_session_nothing_to_deliver() {
        using var tmp = new TempDir();
        const string sid   = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
        const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        var spool = new HookSpool(tmp.Path, time: TimeProvider.System);
        spool.Append(sid, "session-start", "{}");
        spool.Append(other, "session-start", "{}");
        tmp.CreateFile($"{sid}.0-0.draining", "{}");
        tmp.CreateFile($"{sid}.ordered-0-0", "{}");

        spool.Discard(sid);

        await Assert.That(spool.HasBacklog(sid)).IsFalse();
        await Assert.That(spool.SessionIdsWithBacklog()).IsEquivalentTo([other]);
    }

    [Test]
    public async Task A_discard_of_a_session_with_nothing_held_does_nothing() {
        using var tmp = new TempDir();

        new HookSpool(tmp.PathTo("never-made"), time: TimeProvider.System).Discard("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa");

        await Assert.That(Directory.Exists(tmp.PathTo("never-made"))).IsFalse();
    }
}
