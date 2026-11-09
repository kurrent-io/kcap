using System.Net;
using Capacitor.Cli.Core.Harness.Cursor;

namespace Capacitor.Cli.Core.Tests.Unit;

public class LifecycleSpoolDrainTests {
    CursorMarkers Markers => new(Config.Root, TimeProvider.System);

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    // review fix #1/#8 — the HttpClient-backed RunAsync overload's transcript poster
    // (PostTranscript) must enforce the Cursor quarantine marker itself: a batch spooled BEFORE
    // a runtime rewrite-guard trip (or one that simply never got checked anywhere else) must
    // still never reach `/hooks/transcript` once its session is quarantined — this is the ONLY
    // delivery-time check the spool-replay path has.
    [Test]
    public async Task PostTranscript_drops_a_quarantined_cursor_batch_without_posting() {
        using var tmp = new TempDir();
        var sid  = Guid.NewGuid().ToString("N");
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        tx.Append(sid, $$"""{"session_id":"{{sid}}","vendor":"cursor","lines":["x"],"line_numbers":[0]}""");
        Markers.Quarantine(sid, "rewrite detected");

        var posted = new List<string>();
        using var handler = new StubHandler((req, _) => {
            posted.Add(req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, "http://s", life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(posted).DoesNotContain("/hooks/transcript");
        // Dropped (permanently discarded), not left spooled for an endless retry loop.
        await Assert.That(tx.HasBacklog(sid)).IsFalse();
    }

    // Regression guard: a non-quarantined Cursor batch (the overwhelmingly common case) must
    // still post normally — the marker check must not false-positive.
    [Test]
    public async Task PostTranscript_posts_a_non_quarantined_cursor_batch_normally() {
        using var tmp = new TempDir();
        var sid  = Guid.NewGuid().ToString("N");
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        tx.Append(sid, $$"""{"session_id":"{{sid}}","vendor":"cursor","lines":["x"],"line_numbers":[0]}""");

        var posted = new List<string>();
        using var handler = new StubHandler((req, _) => {
            posted.Add(req.RequestUri!.AbsolutePath);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var client = new HttpClient(handler);

        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, "http://s", life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(posted).Contains("/hooks/transcript");
        await Assert.That(tx.HasBacklog(sid)).IsFalse();
    }

    sealed class StubHandler(Func<HttpRequestMessage, string, HttpResponseMessage> impl) : HttpMessageHandler {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            return impl(request, body);
        }
    }

    /// <summary>A 401 is a lapsed credential, which <c>kcap login</c> repairs: the backlog must
    /// outlive it, and a terminal entry that never landed must not mark the session ended.</summary>
    [Test]
    public async Task a_401_keeps_the_backlog_and_does_not_mark_the_session_ended() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
        life.Append(Sid, "session-end/kiro",   """{"phase":"end"}""");

        using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.Unauthorized));
        using var client  = new HttpClient(handler);

        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, "http://s", life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(life.HasBacklog(Sid)).IsTrue();
        await Assert.That(life.IsMarkedEnded(Sid)).IsFalse();
    }

    /// <summary>Non-vacuous control: a 4xx that rejects the payload itself is still dropped, so the
    /// test above proves 401 is singled out rather than that every 4xx is now retained.</summary>
    [Test]
    public async Task a_4xx_that_rejects_the_payload_is_still_dropped() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");

        using var handler = new StubHandler((_, _) => new HttpResponseMessage(HttpStatusCode.BadRequest));
        using var client  = new HttpClient(handler);

        await LifecycleSpoolDrain.RunAsync(Config.Root, Markers, client, "http://s", life, tx, currentSessionId: null,
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(life.HasBacklog(Sid)).IsFalse();
    }

    [Test]
    public async Task drains_start_then_transcript_then_end_for_a_session_with_no_further_hook() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
        tx.Append(Sid, """{"phase":"tail"}""");
        life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

        var order = new List<string>();
        // currentSessionId=null → a DIFFERENT vendor's kcap invocation replaying a prior session.
        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: (route, body) => { order.Add($"L:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEquivalentTo([
            """L:{"phase":"start"}""",
            """T:{"phase":"tail"}""",
            """L:{"phase":"end"}""",
        ]);
    }

    [Test]
    public async Task holds_session_end_when_transcript_backlog_remains() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
        tx.Append(Sid, """{"phase":"tail"}""");
        life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

        var order = new List<string>();
        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: (route, body) => { order.Add($"L:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            // Transcript poster reports a transient failure — the tail never fully drains.
            transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.TransientStop); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        // session-start delivered, transcript attempted, but session-end withheld because the
        // transcript still has backlog — cross-spool ordering must not let session-end race ahead.
        await Assert.That(order).IsEquivalentTo([
            """L:{"phase":"start"}""",
            """T:{"phase":"tail"}""",
        ]);
        await Assert.That(tx.HasBacklog(Sid)).IsTrue();
        await Assert.That(life.HasBacklog(Sid)).IsTrue(); // session-end still spooled
    }

    /// <summary>An owner's marker an abandoned drain left behind is a loss like any other, and a session flagged
    /// twice is still reported once.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task delivers_one_needs_import_for_a_session_an_owners_marker_flags(bool alsoShared) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        tx.MarkNeedsImport(Sid, "drain did not finish", owner: 4242);
        if (alsoShared) tx.MarkNeedsImport(Sid, "shutdown tail: redaction of line 3 timed out");

        var order = new List<string>();
        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: (route, body) => { order.Add($"L:{route}:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEquivalentTo([$"L:session-needs-import:{{\"session_id\":\"{Sid}\",\"needs_import\":true}}"]);
    }

    [Test]
    public async Task delivers_needs_import_marker_even_when_transcript_bytes_exceeded_cap() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), capBytes: 32, time: TimeProvider.System); // tiny cap
        life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
        tx.Append(Sid, "{\"lines\":[\"" + new string('x', 100) + "\"]}"); // exceeds cap → needs-import marker
        life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

        await Assert.That(tx.NeedsImport(Sid)).IsTrue();

        var order = new List<string>();
        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: (route, body) => { order.Add($"L:{route}:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            transcriptPoster: body => { order.Add($"T:{body}"); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEquivalentTo([
            """L:session-start/kiro:{"phase":"start"}""",
            $"L:session-needs-import:{{\"session_id\":\"{Sid}\",\"needs_import\":true}}",
            """L:session-end/kiro:{"phase":"end"}""",
        ]);
    }

    // Task 12 / BLOCKER-3: session-start/subagent-stop/session-end now share one spool
    // file in prod, so a subagent-stop that arrives (or is only discovered) AFTER session-end was
    // already delivered is reachable. Same-pass, DrainRoutesAsync's phase-mismatch break already
    // withholds it correctly — but a bare re-run of RunAsync on a FRESH file containing only that
    // straggler would, without a durable "already ended" marker, treat it as an ordinary phase-1
    // non-terminal entry and deliver it — a real cross-pass ordering violation (the server would
    // see activity for a session it already closed).
    [Test]
    public async Task marks_session_ended_after_terminal_delivery_and_drops_a_later_straggler() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-start/kiro", """{"phase":"start"}""");
        life.Append(Sid, "session-end/kiro",   """{"phase":"end"}""");

        var order = new List<string>();
        Task<DrainOutcome> Deliver(string route, string body) { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); }

        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: Deliver,
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEquivalentTo(["""{"phase":"start"}""", """{"phase":"end"}"""]);
        await Assert.That(life.IsMarkedEnded(Sid)).IsTrue();

        // A late straggler lands in a brand-new pass, with nothing else in its way — the only
        // thing stopping it from looking like an ordinary phase-1 entry is the ended marker.
        life.Append(Sid, "subagent-stop/kiro", """{"phase":"straggler"}""");
        order.Clear();

        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: Deliver,
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEmpty(); // never delivered
        await Assert.That(life.HasBacklog(Sid)).IsFalse(); // discarded, not left pending retry
    }

    // The ordered drain every hook runs, and the route-agnostic one Claude's and Cursor's hooks run: the
    // run-boundary rule is one rule, so each scenario below holds for both.
    static async Task<List<string>> DrainAll(HookSpool life, TranscriptSpool tx, bool fifo = false) {
        using var nothingDisabled = new TempConfigRoot();
        var order = new List<string>();
        Task<DrainOutcome> Deliver(string _, string body) { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); }

        if (fifo) await life.DrainAllAsync(null, Deliver, TimeSpan.FromSeconds(5), CancellationToken.None);
        else await LifecycleSpoolDrain.RunAsync(nothingDisabled.Root, life, tx, currentSessionId: null,
            lifecyclePoster: Deliver,
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        return order;
    }

    static async Task DeliverEnd(HookSpool life, TranscriptSpool tx, bool fifo, string end = """{"run":1,"phase":"end"}""") {
        life.Append(Sid, "session-end/kiro", end);
        await DrainAll(life, tx, fifo);
    }

    /// <summary>A session resumed after its spooled end was delivered: the new run's start and end both
    /// arrive. The earlier run's ended marker must discard neither.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_resumed_run_is_delivered_past_an_earlier_runs_ended_marker(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverEnd(life, tx, fifo);

        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo)))
            .IsEqualTo("""{"run":2,"phase":"start"} {"run":2,"phase":"end"}""");
    }

    /// <summary>A resumed run whose start was posted live spools only its end behind the earlier run's
    /// stragglers: the end is delivered, the stragglers are not.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_later_runs_end_is_delivered_and_the_earlier_runs_stragglers_are_not(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverEnd(life, tx, fifo);

        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"straggler"}""");
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo))).IsEqualTo("""{"run":2,"phase":"end"}""");
    }

    /// <summary>A hosted run whose start was posted live is told from the run whose end was delivered by the
    /// run it names, so its hooks spooled ahead of its own end are not taken for stragglers.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_later_run_named_by_its_hooks_is_not_taken_for_a_straggler(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverEnd(life, tx, fifo, """{"agent_host_id":"a","daemon_epoch":"e1","phase":"end"}""");

        life.Append(Sid, "subagent-stop/kiro", """{"agent_host_id":"a","daemon_epoch":"e1","phase":"straggler"}""");
        life.Append(Sid, "subagent-stop/kiro", """{"agent_host_id":"a","daemon_epoch":"e2","phase":"stop"}""");
        life.Append(Sid, "session-end/kiro", """{"agent_host_id":"a","daemon_epoch":"e2","phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo))).IsEqualTo(
            """{"agent_host_id":"a","daemon_epoch":"e2","phase":"stop"} {"agent_host_id":"a","daemon_epoch":"e2","phase":"end"}""");
    }

    /// <summary>A run whose start was posted live and whose hooks name no run is told from the ended run by
    /// the note that start left.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task hooks_after_a_noted_live_start_are_not_taken_for_stragglers(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverEnd(life, tx, fifo);

        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"straggler"}""");
        life.NoteRunStarted(Sid, life.RunStartSnapshot(Sid));
        life.Append(Sid, "subagent-stop/kiro", """{"run":2,"phase":"stop"}""");
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo)))
            .IsEqualTo("""{"run":2,"phase":"stop"} {"run":2,"phase":"end"}""");
    }

    /// <summary>A start posted live while the earlier run's end is still queued: once the drain delivers that
    /// end, the new run's hooks must still not be taken for its stragglers.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task hooks_after_a_live_start_behind_a_queued_end_are_delivered(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        life.NoteRunStarted(Sid, life.RunStartSnapshot(Sid));
        life.Append(Sid, "subagent-stop/kiro", """{"run":2,"phase":"stop"}""");
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo)))
            .IsEqualTo("""{"run":2,"phase":"stop"} {"run":2,"phase":"end"}""");
    }

    /// <summary>The run's own end, spooled between its start's post and the note that start leaves, belongs to
    /// the run the note opens: only what was queued before the post is superseded.</summary>
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task a_live_starts_note_never_supersedes_that_runs_own_end(bool earlierEndDelivered, bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        if (earlierEndDelivered) await DeliverEnd(life, tx, fifo);
        else life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");

        var snapshot = life.RunStartSnapshot(Sid);
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");
        life.NoteRunStarted(Sid, snapshot);

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo))).IsEqualTo("""{"run":2,"phase":"end"}""");
        await Assert.That(life.HasBacklog(Sid)).IsFalse();
    }

    /// <summary>A note supersedes as many copies of a line as were queued before its start was posted, so a
    /// later run's identical hook is still delivered.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_note_supersedes_only_the_copies_queued_before_its_post(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverEnd(life, tx, fifo);

        life.Append(Sid, "subagent-stop/kiro", """{"same":1}""");
        var snapshot = life.RunStartSnapshot(Sid);
        life.Append(Sid, "subagent-stop/kiro", """{"same":1}""");
        life.NoteRunStarted(Sid, snapshot);

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo))).IsEqualTo("""{"same":1}""");
    }

    /// <summary>An end still spooled when the session starts again would be delivered ahead of or after that
    /// start, ending the resumed run, and the marker it left would then discard the run's own hooks.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_start_supersedes_an_end_spooled_before_it(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"stop"}""");
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo)))
            .IsEqualTo("""{"run":1,"phase":"stop"} {"run":2,"phase":"start"}""");
        await Assert.That(life.IsMarkedEnded(Sid)).IsFalse();
    }

    /// <summary>The same, with the earlier end already taken into a drain that could not deliver it, of either kind.</summary>
    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task a_start_supersedes_an_end_a_drain_holds_in_its_temp(bool heldByFifo, bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        static Task<DrainOutcome> Refuse(string _, string __) => Task.FromResult(DrainOutcome.TransientStop);
        if (heldByFifo) await life.DrainAllAsync(null, Refuse, TimeSpan.FromSeconds(5), CancellationToken.None);
        else await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null, lifecyclePoster: Refuse,
            transcriptPoster: _ => Task.FromResult(DrainOutcome.Delivered),
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo))).IsEqualTo("""{"run":2,"phase":"start"}""");
        await Assert.That(life.HasBacklog(Sid)).IsFalse();
    }

    /// <summary>Two temps one process made can share a creation time: they keep the order the process made them in.</summary>
    [Test]
    public async Task temps_with_one_creation_time_keep_the_order_they_were_made_in() {
        using var tmp = new TempDir();
        var held  = tmp.CreateFile($"{Sid}.7-1.draining", "{\"route\":\"session-end/kiro\",\"body\":\"{\\\"run\\\":1}\"}\n");
        var later = tmp.CreateFile($"{Sid}.ordered-7-2", "{\"route\":\"session-start/kiro\",\"body\":\"{\\\"run\\\":2}\"}\n");
        var at    = DateTime.UtcNow.AddMinutes(-1);
        File.SetCreationTimeUtc(held, at);
        File.SetCreationTimeUtc(later, at);
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);

        await Assert.That(string.Join(" ", await DrainAll(life, tx))).IsEqualTo("""{"run":2}""");
        await Assert.That(life.HasBacklog(Sid)).IsFalse();
    }

    /// <summary>A straggler of an ended run is never delivered after that end, not even ahead of a new start.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task an_ended_runs_straggler_is_not_delivered_ahead_of_a_new_start(bool fifo) {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverEnd(life, tx, fifo);

        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"straggler"}""");
        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx, fifo))).IsEqualTo("""{"run":2,"phase":"start"}""");
    }

    /// <summary>Other processes append to the same file with no lock, so an append that rewrote it could lose
    /// theirs: appending only ever adds a line.</summary>
    [Test]
    public async Task appending_a_start_keeps_every_line_already_spooled() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        var lines = await File.ReadAllLinesAsync(tmp.PathTo($"{Sid}.jsonl"));
        await Assert.That(lines.Length).IsEqualTo(2);
    }

    [Test]
    public async Task does_not_mark_ended_when_the_terminal_post_only_transiently_fails() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", """{"phase":"end"}""");

        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: null,
            lifecyclePoster: (_, _) => Task.FromResult(DrainOutcome.TransientStop),
            transcriptPoster: _ => Task.FromResult(DrainOutcome.Delivered),
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        // A transient failure must not be mistaken for "session ended" — the entry stays
        // spooled so a later pass can retry it, exactly as before.
        await Assert.That(life.IsMarkedEnded(Sid)).IsFalse();
        await Assert.That(life.HasBacklog(Sid)).IsTrue();
    }

    [Test]
    public async Task current_session_id_drains_first() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        life.Append(other, "session-start/kiro", """{"who":"other"}""");
        life.Append(Sid, "session-start/kiro", """{"who":"current"}""");

        var order = new List<string>();
        await LifecycleSpoolDrain.RunAsync(Config.Root, life, tx, currentSessionId: Sid,
            lifecyclePoster: (route, body) => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order[0]).IsEqualTo("""{"who":"current"}""");
        await Assert.That(order).Contains("""{"who":"other"}""");
    }

    const string Spooled = """{"session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"}""";
    const string Tail    = """{"session_id":"aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa","lines":["x"],"line_numbers":[0]}""";

    HookSpool Lifecycle => new(Config.Root, TimeProvider.System, "http://s");

    TranscriptSpool Transcript => new(Config.Root, TimeProvider.System, "http://s");

    readonly List<string> _posted = [];

    Task<DrainOutcome> PostLifecycle(string route, string body) {
        _posted.Add(route);

        return Task.FromResult(DrainOutcome.Delivered);
    }

    Task<DrainOutcome> PostTail(string body) {
        _posted.Add("transcript");

        return Task.FromResult(DrainOutcome.Delivered);
    }

    Task Drain() => LifecycleSpoolDrain.RunAsync(
        Config.Root, Lifecycle, Transcript, currentSessionId: null, PostLifecycle, PostTail, TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);

    /// <summary>The check sits here because a discard at the disable covers only what a daemon held at that moment. A
    /// hook's entries, and a tail left by a source that ended long before, wait for this pass.</summary>
    [Test]
    public async Task Nothing_of_a_disabled_session_is_posted_and_both_spools_go() {
        Lifecycle.Append(Sid, "session-start", Spooled);
        Lifecycle.Append(Sid, "session-end", Spooled);
        Transcript.Append(Sid, Tail);
        Transcript.MarkNeedsImport(Sid, "left by an earlier stop");
        DisabledSessions.Mark(Sid, Config.Root);

        await Drain();

        await Assert.That(_posted).IsEmpty();
        await Assert.That(Lifecycle.HasBacklog(Sid)).IsFalse();
        await Assert.That(Transcript.HasBacklog(Sid)).IsFalse();
        await Assert.That(Transcript.NeedsImport(Sid)).IsFalse();
    }

    /// <summary>An exclusion only quiets the hooks: what it spooled before the repository left scope is still recorded.</summary>
    [Test]
    public async Task An_excluded_sessions_spools_drain_as_ever() {
        Lifecycle.Append(Sid, "session-start", Spooled);
        Transcript.Append(Sid, Tail);
        DisabledSessions.Exclude(Sid, Config.Root);

        await Drain();

        await Assert.That(_posted).Contains("transcript");
        await Assert.That(_posted).Contains("session-start");
    }

    [Test]
    public async Task Another_sessions_spools_are_left_to_drain() {
        const string other = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
        Lifecycle.Append(Sid, "session-start", Spooled);
        Lifecycle.Append(other, "session-start", Spooled);
        DisabledSessions.Mark(Sid, Config.Root);

        await Drain();

        await Assert.That(_posted).IsEquivalentTo(["session-start"]);
        await Assert.That(Lifecycle.HasBacklog(other)).IsFalse();
    }

    /// <summary>Files from before the per-server directories sit in the root, where only a drain's adoption reaches them.</summary>
    [Test]
    public async Task A_disabled_sessions_files_in_the_legacy_root_go_too() {
        Directory.CreateDirectory(Config.Root.Path(HookSpool.DirectoryName));
        Directory.CreateDirectory(Config.Root.Path(TranscriptSpool.DirectoryName));
        await File.WriteAllTextAsync(Config.Root.Path(HookSpool.DirectoryName, $"{Sid}.jsonl"), """{"route":"session-start","body":"{}"}""" + "\n");
        await File.WriteAllTextAsync(Config.Root.Path(TranscriptSpool.DirectoryName, $"{Sid}.transcript.jsonl"), Tail + "\n");
        DisabledSessions.Mark(Sid, Config.Root);

        await Drain();

        await Assert.That(_posted).IsEmpty();
        await Assert.That(Lifecycle.HasBacklog(Sid)).IsFalse();
        await Assert.That(Transcript.HasBacklog(Sid)).IsFalse();
    }

    /// <summary>A transient stop writes the remainder back, which restores what a discard had deleted under that pass.</summary>
    [Test]
    public async Task A_tail_a_transient_stop_wrote_back_goes_on_the_next_pass() {
        Transcript.Append(Sid, Tail);
        await LifecycleSpoolDrain.RunAsync(
            Config.Root, Lifecycle, Transcript, currentSessionId: null, PostLifecycle, _ => Task.FromResult(DrainOutcome.TransientStop),
            TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);
        await Assert.That(Transcript.HasBacklog(Sid)).IsTrue();

        DisabledSessions.Mark(Sid, Config.Root);
        await Drain();

        await Assert.That(_posted).IsEmpty();
        await Assert.That(Transcript.HasBacklog(Sid)).IsFalse();
    }

    /// <summary>A pass already under way when the person disables the session stops at its next post.</summary>
    [Test]
    public async Task A_session_disabled_during_a_pass_posts_nothing_more() {
        Transcript.Append(Sid, Tail);
        Transcript.Append(Sid, Tail);

        await LifecycleSpoolDrain.RunAsync(
            Config.Root, Lifecycle, Transcript, currentSessionId: null, PostLifecycle,
            body => {
                DisabledSessions.Mark(Sid, Config.Root);

                return PostTail(body);
            },
            TimeSpan.FromSeconds(30), TimeProvider.System, CancellationToken.None);

        await Assert.That(_posted.Count).IsEqualTo(1);
    }

    /// <summary><c>kcap disable</c> marks a session by its canonical id, and a spool keeps the id as the hook spelled it.</summary>
    [Test]
    public async Task A_marker_covers_any_spelling_of_the_session() {
        const string dashed = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        Lifecycle.Append(dashed, "session-start", Spooled);
        DisabledSessions.Mark(Sid, Config.Root);

        await Drain();

        await Assert.That(_posted).IsEmpty();
        await Assert.That(Lifecycle.HasBacklog(dashed)).IsFalse();
    }
}
