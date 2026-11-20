using Capacitor.Cli.Core.Harness.OpenCode;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.OpenCode;

/// <summary>
/// The plugin's OpenCode v2 entry point, pinned at source level: the plugin is TypeScript with no
/// runtime in this suite.
/// </summary>
public class OpenCodePluginV2Tests {
    const string Content = OpenCodeExtensionInstaller.ExtensionContent;

    static string V2 => Content[Content.IndexOf("async function v2Setup(", StringComparison.Ordinal)..];

    static string Between(string text, string from, string to) {
        var start = text.IndexOf(from, StringComparison.Ordinal);
        var end   = start < 0 ? -1 : text.IndexOf(to, start + from.Length, StringComparison.Ordinal);
        return end > start ? text[start..end] : "";
    }

    /// <summary>v2 reads only the default export's <c>{id, setup}</c>; v1 from 1.18.29 reads its
    /// <c>server</c>. Both must be the one file, with no import of OpenCode's own packages.</summary>
    [Test]
    public async Task One_file_exports_both_entry_points_without_dependencies() {
        await Assert.That(Content).Contains("export default { id: \"kcap\", server: KcapPlugin, setup: v2Setup }");
        await Assert.That(Content).Contains("export const KcapPlugin");
        await Assert.That(Content).DoesNotContain("from \"@opencode");
    }

    /// <summary>v1 calls setup as well, with a context lacking session, tool and event; v2 never calls
    /// server. Whichever runs first owns the process, so no session is started twice.</summary>
    [Test]
    public async Task Only_one_api_runs_in_a_process() {
        await Assert.That(Content).Contains("if (api === \"v2\") return {}");
        await Assert.That(V2).Contains(
            "if (api === \"v1\" || !ctx?.session?.hook || !ctx?.tool?.hook || !ctx?.event?.subscribe) return");
        await Assert.That(V2.IndexOf("api = \"v2\"", StringComparison.Ordinal))
            .IsGreaterThan(V2.IndexOf("if (!shell) return", StringComparison.Ordinal));
    }

    /// <summary>Only a session the server reports without a parent is started; a failed lookup starts
    /// nothing, and a later event retries it after a pause rather than never.</summary>
    [Test]
    public async Task A_session_starts_only_once_proven_top_level() {
        var gate = Between(V2, "function ensureStarted(", "async function onEvent(");

        await Assert.That(gate).Contains("try { info = await ctx.session.get({ sessionID: sid }) } catch {}");
        await Assert.That(gate).Contains("if (!info?.id) { v2Remember(v2.retryAt, sid, Date.now() + V2_RETRY_MS, V2_MAX_IDS); return false }");
        await Assert.That(Between(gate, "if (info.parentID) {", "return false")).Contains("v2Remember(v2.children, sid, true, V2_MAX_IDS)");
        await Assert.That(gate).Contains("\"hook\", \"--opencode\", \"--event\", \"session-start\"");
        await Assert.That(gate).Contains("\"--memory-contract\", \"1\"");
        await Assert.That(gate).Contains("if (fragment.startsWith(MEMORY_MARKER) && !v2.memory.has(sid)) {");
        await Assert.That(Between(V2, "if (type === \"session.created\" && data?.parentID) {", "return")).Contains("v2Remember(v2.children, sid, true, V2_MAX_IDS)");
    }

    /// <summary>A session is marked started before its lifecycle post returns, so a caller must find the
    /// in-flight start first: otherwise the first request misses the index and a tool call is stamped
    /// before the session exists on the server.</summary>
    [Test]
    public async Task An_in_flight_start_is_waited_for_before_it_reads_as_started() {
        var gate = Between(V2, "function ensureStarted(", "async function onEvent(");

        var pending = gate.IndexOf("v2.pendingStart.get(sid)", StringComparison.Ordinal);
        var started = gate.IndexOf("if (v2.started.has(sid)) return Promise.resolve(true)", StringComparison.Ordinal);

        await Assert.That(pending).IsGreaterThanOrEqualTo(0);
        await Assert.That(started).IsGreaterThanOrEqualTo(0);
        await Assert.That(pending).IsLessThan(started);
        await Assert.That(V2).Contains("if (pending) await v2Wait(sid, pending)");
    }

    /// <summary>The server process outlives its sessions, most of which never send session.deleted, so
    /// every per-session collection is bounded.</summary>
    [Test]
    public async Task Session_state_is_bounded() {
        await Assert.That(Content).Contains("started: new Map<string, true>()");
        await Assert.That(Content).Contains("children: new Map<string, true>()");
        await Assert.That(V2).DoesNotContain("v2.started.add(");
        await Assert.That(V2).DoesNotContain("v2.children.add(");
    }

    /// <summary>A start stays in flight until kcap settles: a late failure is still retried, a late
    /// fragment still cached, and no second start can overlap a running one. A hung kcap is waited on
    /// once per session, so it cannot stall every request.</summary>
    [Test]
    public async Task A_start_stays_in_flight_until_kcap_settles() {
        var gate = Between(V2, "function ensureStarted(", "async function onEvent(");

        await Assert.That(Content).Contains("const res: any = await shell`kcap ${args}`.quiet().nothrow()");
        await Assert.That(Between(Content, "async function v2RunKcap(", "async function v2Setup(")).DoesNotContain("Promise.race");
        await Assert.That(gate).Contains("const result = await v2RunKcap(shell, args)");
        await Assert.That(gate).Contains("if (result.failed) {");
        await Assert.That(gate).Contains("v2.started.delete(sid)");
        await Assert.That(Content).Contains("if (v2.waitExpired.has(sid)) return");
        await Assert.That(gate).Contains("v2.waitExpired.delete(sid)");
    }

    /// <summary>A fragment evicted from the bounded cache must not leave its session reading as started,
    /// or that session's later requests never get the index again.</summary>
    [Test]
    public async Task An_evicted_fragment_restarts_its_session() {
        await Assert.That(V2).Contains("const evicted = v2Remember(v2.memory, sid, fragment)");
        await Assert.That(V2).Contains("if (evicted) v2.started.delete(evicted)");
    }

    /// <summary>A session's title is held until the session is proven top-level, and written once it is, by the event
    /// that brought it or by the start that proved it.</summary>
    [Test]
    public async Task A_title_is_written_once_the_session_is_proven() {
        await Assert.That(V2).Contains("v2Remember(v2.pendingTitle, sid, { title: data.title, time: ev?.created })");
        await Assert.That(V2).Contains("await v2Wait(sid, gate)");
        await Assert.That(V2).Contains("if (!v2.started.has(sid) || v2.pendingStart.get(sid) === gate) return");
        await Assert.That(Between(V2, "async function onEvent(", "ctx.session.hook(")).DoesNotContain("await ensureStarted(");
        await Assert.That(Between(V2, "function ensureStarted(", "async function onEvent(")).Contains("flushTitle(sid)");
        await Assert.That(V2).Contains("{ type: \"session_title\", title }");
    }

    /// <summary>After each step and each turn a recorded session's context is read and every user, assistant and shell
    /// message that settled since is appended as it was returned, the reads of one file in turn and a message marked
    /// written only once its append succeeded.</summary>
    [Test]
    public async Task Each_settled_message_is_appended_after_a_step_or_a_turn() {
        var capture = Between(V2, "async function capture(", "function enqueueCapture(");
        var events  = Between(Content, "const V2_CAPTURE_EVENTS", "])");

        await Assert.That(Content).Contains("const V2_RECORDED = new Set([\"user\", \"assistant\", \"shell\"])");
        foreach (var type in new[] { "session.step.ended", "session.step.failed", "session.shell.ended", "session.execution.succeeded",
                                     "session.execution.failed", "session.execution.interrupted" })
            await Assert.That(events).Contains($"\"{type}\"");
        await Assert.That(Between(Content, "function v2SettledAt(", "function v2Key(")).Contains(
            "const at = m?.type === \"user\" ? m?.time?.created : m?.time?.completed");
        await Assert.That(capture).Contains("res = await ctx.session.context({ sessionID: sid })");
        await Assert.That(capture).Contains("pending.push({ key, line: JSON.stringify(m) })");
        await Assert.That(capture.IndexOf("appendFileSync(target", StringComparison.Ordinal))
            .IsLessThan(capture.IndexOf("for (const p of pending) seen.add(p.key)", StringComparison.Ordinal));
        await Assert.That(V2).Contains("(v2.capturing.get(target) ?? Promise.resolve()).then(() => capture(sid, target))");
        await Assert.That(V2).Contains("if (V2_CAPTURE_EVENTS.has(type)) enqueueCapture(sid, file(sid))");
    }

    /// <summary>A start that outlasts an event's wait leaves that event's capture undone, and a subagent's events find
    /// its parent unstarted, so a successful start reads the session and its known subagents once more.</summary>
    [Test]
    public async Task A_successful_start_captures_what_settled_while_it_ran() {
        var start = Between(Between(V2, "function ensureStarted(", "async function onEvent("), "flushTitle(sid)", "return true");

        await Assert.That(start).Contains("enqueueCapture(sid, file(sid))");
        await Assert.That(start).Contains("for (const [child, parent] of v2.parentOf) if (parent === sid) captureChild(child)");
    }

    /// <summary>A message that settled before the plugin loaded is history, which import reads: a resumed session, or
    /// one copied from v1 with its v1 messages under new ids, never has its backlog appended.</summary>
    [Test]
    public async Task A_message_settled_before_the_plugin_loaded_is_not_appended() {
        await Assert.That(V2).Contains("const loadedAt = Date.now()");
        await Assert.That(Between(V2, "async function capture(", "function enqueueCapture(")).Contains(
            "if (at === undefined || at < loadedAt) continue");
    }

    /// <summary>A message is appended again only when more of it has settled; the projection's ids come from the
    /// message, so a repeat adds only the new parts.</summary>
    [Test]
    public async Task A_message_is_appended_again_only_when_more_of_it_settled() {
        var key = Between(Content, "function v2Key(", "const V2_MAX_SESSIONS");

        await Assert.That(key).Contains("if (p?.type === \"tool\" && (st === \"completed\" || st === \"error\")) settledTools++");
        await Assert.That(key).Contains("return m.id + \":\" + content.length + \":\" + settledTools");
    }

    /// <summary>A subagent of a recorded session is appended beside its parent's file, where the watcher looks, and is
    /// never started as a session of its own; a deeper one is not recorded, as on v1.</summary>
    [Test]
    public async Task A_subagents_messages_go_beside_its_parents_file() {
        await Assert.That(V2).Contains("const childFile = (parent: string, child: string) => join(dir, parent, child + \".jsonl\")");
        await Assert.That(Between(V2, "function captureChild(", "async function onEvent(")).Contains(
            "if (parent && v2.started.has(parent)) enqueueCapture(sid, childFile(parent, sid))");
        await Assert.That(V2).Contains("v2Remember(v2.parentOf, sid, String(data.parentID), V2_MAX_IDS)");
        await Assert.That(Between(V2, "function ensureStarted(", "async function onEvent(")).Contains(
            "v2Remember(v2.parentOf, sid, String(info.parentID), V2_MAX_IDS)");
        await Assert.That(Between(V2, "async function onEvent(", "function flushTitle(")).Contains(
            "if (V2_CAPTURE_EVENTS.has(type)) captureChild(sid)");
    }

    /// <summary>The context hook appends the index as one more system part, once per request, never for
    /// a subagent, and never throws into the model request.</summary>
    [Test]
    public async Task The_memory_index_is_appended_to_each_request() {
        var hook = Between(V2, "ctx.session.hook(\"context\"", "ctx.tool.hook(");

        await Assert.That(hook).Contains("if (!sid || v2.children.has(sid) || !Array.isArray(system)) return");
        await Assert.That(hook).Contains("p.text.includes(MEMORY_MARKER)");
        await Assert.That(hook).Contains("system.push({ type: \"text\", text: fragment })");
        await Assert.That(hook).Contains("} catch {");
    }

    /// <summary>Only a server shared across sessions writes the heartbeat that makes kcap end a session on idle, and
    /// writes it before kcap starts the watcher that reads it. A standalone run's private server exits with the run,
    /// so it clears one a shared server left rather than leave its session to end on idle while open.</summary>
    [Test]
    public async Task Only_a_shared_server_declares_its_sessions_end_on_idle() {
        var gate = Between(V2, "function ensureStarted(", "async function onEvent(");

        await Assert.That(V2).Contains("const shared = !process.argv.includes(\"--stdio\")");
        await Assert.That(gate).Contains("if (shared) writeHeartbeat(sid)");
        await Assert.That(gate).Contains("rmSync(heartbeat(sid), { force: true })");
        await Assert.That(gate).Contains("rmSync(ended(sid), { force: true })");
        await Assert.That(gate.IndexOf("if (shared) writeHeartbeat(sid)", StringComparison.Ordinal))
            .IsLessThan(gate.IndexOf("await v2RunKcap(shell, args)", StringComparison.Ordinal));
    }

    /// <summary>kcap reads the heartbeat beside the session's file, in the round-trip format it writes its own, and
    /// writes the end marker beside it.</summary>
    [Test]
    public async Task The_heartbeat_and_end_marker_are_where_kcap_expects_them() {
        await Assert.That(V2).Contains("const file = (sid: string) => join(dir, sid + \".jsonl\")");
        await Assert.That(V2).Contains("const heartbeat = (sid: string) => join(dir, sid + \".heartbeat\")");
        await Assert.That(V2).Contains("const ended = (sid: string) => join(dir, sid + \".ended\")");
        await Assert.That(OpenCodeSessionHeartbeat.PathFor(Path.Combine("cache", "ses_a.jsonl")))
            .IsEqualTo(Path.Combine("cache", "ses_a.heartbeat"));
        await Assert.That(OpenCodeSessionHeartbeat.EndedPathFor(Path.Combine("cache", "ses_a.jsonl")))
            .IsEqualTo(Path.Combine("cache", "ses_a.ended"));

        var write = Between(V2, "function writeHeartbeat(", "function beat(");
        await Assert.That(write).Contains("writeFileSync(tmp, new Date().toISOString())");
        await Assert.That(write).Contains("renameSync(tmp, heartbeat(sid))");
    }

    /// <summary>Every event of a top-level session beats, before the event's start check. While kcap's end marker
    /// exists, every event looks for it, unthrottled, and unmarks the session at most once per retry gap, so that
    /// event's start check starts it again until a recorder runs and removes the marker.</summary>
    [Test]
    public async Task Session_events_beat_and_a_session_ended_on_idle_starts_again() {
        var onEvent = Between(V2, "async function onEvent(", "function flushTitle(");
        var beat    = Between(V2, "function beat(", "let toolTimer");

        await Assert.That(onEvent.IndexOf("beat(sid)", StringComparison.Ordinal))
            .IsGreaterThan(onEvent.IndexOf("if (v2.children.has(sid)) return", StringComparison.Ordinal));
        await Assert.That(onEvent.IndexOf("beat(sid)", StringComparison.Ordinal))
            .IsLessThan(onEvent.IndexOf("ensureStarted(sid", StringComparison.Ordinal));
        await Assert.That(beat).Contains(
            "if (!v2.pendingStart.has(sid) && existsSync(ended(sid)) && (v2.restartAt.get(sid) ?? 0) <= Date.now()) {");
        await Assert.That(beat).Contains("v2.started.delete(sid)");
        await Assert.That(beat.IndexOf("existsSync(ended(sid))", StringComparison.Ordinal))
            .IsLessThan(beat.IndexOf("V2_BEAT_MS) return", StringComparison.Ordinal));
    }

    /// <summary>A long tool sends nothing between its call and its result, so the heartbeat is refreshed on a timer
    /// until every call of the session settles or its execution ends.</summary>
    [Test]
    public async Task A_tool_in_flight_keeps_its_session_beating() {
        var tools = Between(V2, "function trackTools(", "function ensureStarted(");

        await Assert.That(tools).Contains("type === \"session.tool.called\"");
        await Assert.That(tools).Contains("type === \"session.tool.success\" || type === \"session.tool.failed\"");
        await Assert.That(tools).Contains("type.startsWith(\"session.execution.\") && type !== \"session.execution.started\"");
        await Assert.That(tools).Contains("setInterval(() => { for (const id of v2.tools.keys()) beat(id, true) }, V2_TOOL_BEAT_MS)");
        await Assert.That(tools).Contains("clearInterval(toolTimer)");
    }

    /// <summary>Code Mode's MCP calls reach execute.before under the v1 names, so the same tool set is
    /// stamped, in place, only for a proven top-level session that named none.</summary>
    [Test]
    public async Task Kcap_mcp_calls_are_stamped_with_the_calling_session() {
        var hook = V2[V2.IndexOf("ctx.tool.hook(\"execute.before\"", StringComparison.Ordinal)..];

        await Assert.That(hook).Contains("if (!sid || !CALLER_SESSION_TOOLS.has(call?.tool)) return");
        await Assert.That(hook).Contains("""if (input.session_id !== undefined && input.session_id !== null && input.session_id !== "") return""");
        await Assert.That(hook).Contains("if (!v2.started.has(sid) || v2.children.has(sid)) return");
        await Assert.That(hook).Contains("input.session_id = sid");
        await Assert.That(hook).DoesNotContain("call.input =");
        await Assert.That(hook).Contains("} catch {");
    }
}
