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
        await Assert.That(gate).Contains("if (info.parentID) { v2Remember(v2.children, sid, true, V2_MAX_IDS); return false }");
        await Assert.That(gate).Contains("\"hook\", \"--opencode\", \"--event\", \"session-start\"");
        await Assert.That(gate).Contains("\"--memory-contract\", \"1\"");
        await Assert.That(gate).Contains("if (fragment.startsWith(MEMORY_MARKER) && !v2.memory.has(sid)) {");
        await Assert.That(V2).Contains("if (type === \"session.created\" && data?.parentID) { v2Remember(v2.children, sid, true, V2_MAX_IDS); return }");
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

    /// <summary>v2 transcripts are not captured yet: the session's file only ever gets title lines.</summary>
    [Test]
    public async Task No_transcript_is_written_on_v2() {
        await Assert.That(V2).DoesNotContain("session.messages");
        await Assert.That(V2).DoesNotContain("writeMessages");
        await Assert.That(V2).Contains("v2Remember(v2.pendingTitle, sid, { title: data.title, time: ev?.created })");
        await Assert.That(V2).Contains("await v2Wait(sid, gate)");
        await Assert.That(V2).Contains("if (!v2.started.has(sid) || v2.pendingStart.get(sid) === gate) return");
        await Assert.That(Between(V2, "async function onEvent(", "ctx.session.hook(")).DoesNotContain("await ensureStarted(");
        await Assert.That(Between(V2, "function ensureStarted(", "async function onEvent(")).Contains("flushTitle(sid)");
        await Assert.That(V2).Contains("{ type: \"session_title\", title }");
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
