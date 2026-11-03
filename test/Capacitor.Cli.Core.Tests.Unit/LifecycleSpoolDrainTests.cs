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

        await LifecycleSpoolDrain.RunAsync(Markers, client, "http://s", life, tx, currentSessionId: null,
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

        await LifecycleSpoolDrain.RunAsync(Markers, client, "http://s", life, tx, currentSessionId: null,
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

        await LifecycleSpoolDrain.RunAsync(Markers, client, "http://s", life, tx, currentSessionId: null,
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

        await LifecycleSpoolDrain.RunAsync(Markers, client, "http://s", life, tx, currentSessionId: null,
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
        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
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
        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
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
        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
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

        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
            lifecyclePoster: Deliver,
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEquivalentTo(["""{"phase":"start"}""", """{"phase":"end"}"""]);
        await Assert.That(life.IsMarkedEnded(Sid)).IsTrue();

        // A late straggler lands in a brand-new pass, with nothing else in its way — the only
        // thing stopping it from looking like an ordinary phase-1 entry is the ended marker.
        life.Append(Sid, "subagent-stop/kiro", """{"phase":"straggler"}""");
        order.Clear();

        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
            lifecyclePoster: Deliver,
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order).IsEmpty(); // never delivered
        await Assert.That(life.HasBacklog(Sid)).IsFalse(); // discarded, not left pending retry
    }

    static async Task<List<string>> DrainAll(HookSpool life, TranscriptSpool tx) {
        var order = new List<string>();
        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
            lifecyclePoster: (_, body) => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        return order;
    }

    static async Task DeliverRun1(HookSpool life, TranscriptSpool tx) {
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        await DrainAll(life, tx);
    }

    /// <summary>A session resumed after its spooled end was delivered: the new run's start and end both
    /// arrive. The earlier run's ended marker must discard neither.</summary>
    [Test]
    public async Task a_resumed_run_is_delivered_past_an_earlier_runs_ended_marker() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverRun1(life, tx);

        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx)))
            .IsEqualTo("""{"run":2,"phase":"start"} {"run":2,"phase":"end"}""");
    }

    /// <summary>A resumed run whose start was posted live spools only its end behind the earlier run's
    /// stragglers: the end is delivered, the stragglers are not.</summary>
    [Test]
    public async Task a_later_runs_end_is_delivered_and_the_earlier_runs_stragglers_are_not() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverRun1(life, tx);

        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"straggler"}""");
        life.Append(Sid, "session-end/kiro", """{"run":2,"phase":"end"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx))).IsEqualTo("""{"run":2,"phase":"end"}""");
    }

    /// <summary>An end still spooled when the session starts again would be delivered after that start,
    /// ending the resumed run, and the marker it left would then discard the run's own hooks.</summary>
    [Test]
    public async Task a_start_supersedes_an_end_spooled_before_it() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"stop"}""");
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx)))
            .IsEqualTo("""{"run":1,"phase":"stop"} {"run":2,"phase":"start"}""");
        await Assert.That(life.IsMarkedEnded(Sid)).IsFalse();
    }

    /// <summary>The same, with the earlier end already taken into a drain that could not deliver it.</summary>
    [Test]
    public async Task a_start_supersedes_an_end_a_drain_holds_in_its_temp() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        life.Append(Sid, "session-end/kiro", """{"run":1,"phase":"end"}""");
        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
            lifecyclePoster: (_, _) => Task.FromResult(DrainOutcome.TransientStop),
            transcriptPoster: _ => Task.FromResult(DrainOutcome.Delivered),
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx))).IsEqualTo("""{"run":2,"phase":"start"}""");
    }

    /// <summary>A straggler of an ended run is never delivered after that end, not even ahead of a new start.</summary>
    [Test]
    public async Task an_ended_runs_straggler_is_not_delivered_ahead_of_a_new_start() {
        using var tmp = new TempDir();
        var life = new HookSpool(tmp.Path, time: TimeProvider.System);
        var tx   = new TranscriptSpool(tmp.PathTo("tx"), time: TimeProvider.System);
        await DeliverRun1(life, tx);

        life.Append(Sid, "subagent-stop/kiro", """{"run":1,"phase":"straggler"}""");
        life.Append(Sid, "session-start/kiro", """{"run":2,"phase":"start"}""");

        await Assert.That(string.Join(" ", await DrainAll(life, tx))).IsEqualTo("""{"run":2,"phase":"start"}""");
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

        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: null,
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
        await LifecycleSpoolDrain.RunAsync(life, tx, currentSessionId: Sid,
            lifecyclePoster: (route, body) => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            transcriptPoster: body => { order.Add(body); return Task.FromResult(DrainOutcome.Delivered); },
            budget: TimeSpan.FromSeconds(5), ct: CancellationToken.None, time: TimeProvider.System);

        await Assert.That(order[0]).IsEqualTo("""{"who":"current"}""");
        await Assert.That(order).Contains("""{"who":"other"}""");
    }
}
