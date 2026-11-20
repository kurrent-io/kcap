using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts.Tests.Unit;

/// Each derivation is a persistence contract: the server dedups by these ids, so the bytes hashed
/// are pinned here twice — once by framing (the exact byte layout) and once by fixed vectors.
public class TranscriptIdsTests {
    static readonly Guid Primary = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

    const string GrokLine = """{"method":"session/update","params":{"sessionId":"s","update":{"sessionUpdate":"agent_message_chunk","content":{"type":"text","text":"x"}}}}""";
    const string AntigravityPlanner = """{"step_index":9,"type":"PLANNER_RESPONSE","tool_calls":[{"name":"run_command","args":{}}]}""";

    [Test]
    public async Task Sibling_hashes_the_primary_guid_bytes_then_the_utf8_suffix() {
        var expectedInput = new byte[16 + 6];
        Primary.TryWriteBytes(expectedInput);
        Encoding.UTF8.GetBytes("result").CopyTo(expectedInput, 16);

        await Assert.That(TranscriptIds.Sibling(Primary, "result")).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
        await Assert.That(TranscriptIds.Sibling(Primary, "result")).IsNotEqualTo(TranscriptIds.Sibling(Primary, "usage-backfill"));
    }

    [Test]
    public async Task Claude_fallback_hashes_line_number_space_line() {
        var expected = new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("12 {\"type\":\"user\"}")));
        await Assert.That(TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}")).IsEqualTo(expected);
        await Assert.That(TranscriptIds.ClaudeFallback(13, "{\"type\":\"user\"}")).IsNotEqualTo(expected);
    }

    [Test]
    public async Task Claude_block_is_the_sibling_with_a_block_suffix() {
        await Assert.That(TranscriptIds.ClaudeBlock(Primary, 2)).IsEqualTo(TranscriptIds.Sibling(Primary, "block:2"));
    }

    /// kcap-server derives the same id from the session's canonical id, and refuses a canonical batch carrying another.
    [Test]
    [Arguments("5f6c2b1a-9e3d-4a7c-bb12-1c2d3e4f5a6b")]
    [Arguments("5F6C2B1A9E3D4A7CBB121C2D3E4F5A6B")]
    [Arguments("5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b")]
    public async Task Claude_attachment_hashes_the_dashless_session_then_record_guid_then_little_endian_index(string sessionId) {
        await Assert.That(TranscriptIds.ClaudeAttachment(sessionId, Primary, 3))
            .IsEqualTo(AttachmentHash("5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b", Primary, 3));
    }

    [Test]
    public async Task Claude_attachment_hashes_a_session_id_that_is_no_guid_as_given() {
        await Assert.That(TranscriptIds.ClaudeAttachment("sess-1", Primary, 3)).IsEqualTo(AttachmentHash("sess-1", Primary, 3));
    }

    static Guid AttachmentHash(string session, Guid recordId, int index) {
        var scope = Encoding.UTF8.GetBytes(session);
        var expectedInput = new byte[scope.Length + 20];
        scope.CopyTo(expectedInput, 0);
        recordId.TryWriteBytes(expectedInput.AsSpan(scope.Length));
        BinaryPrimitives.WriteInt32LittleEndian(expectedInput.AsSpan(scope.Length + 16), index);

        return new Guid(XxHash128.Hash(expectedInput));
    }

    [Test]
    public async Task Claude_retraction_hashes_an_r_byte_then_the_cancelled_prompt_guid() {
        var expectedInput = new byte[17];
        expectedInput[0] = (byte)'r';
        Primary.TryWriteBytes(expectedInput.AsSpan(1));

        await Assert.That(TranscriptIds.ClaudeRetraction(Primary)).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
    }

    [Test]
    public async Task Codex_record_hashes_the_utf8_line() {
        const string line = "{\"type\":\"response_item\",\"payload\":{}}";
        await Assert.That(TranscriptIds.CodexRecord(line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line))));
    }

    [Test]
    public async Task Gemini_record_is_its_guid_id_else_the_hash_of_the_utf8_line() {
        const string line = "{\"id\":\"n1\",\"type\":\"user\"}";
        await Assert.That(TranscriptIds.GeminiRecord("a3b19e5f-275a-407f-891f-9b3661d7e79b", line)).IsEqualTo(Guid.Parse("a3b19e5f-275a-407f-891f-9b3661d7e79b"));
        await Assert.That(TranscriptIds.GeminiRecord("d04923d38bb0f6017037e74183378ef4", line)).IsEqualTo(Guid.Parse("d04923d3-8bb0-f601-7037-e74183378ef4"));
        await Assert.That(TranscriptIds.GeminiRecord("n1", line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line))));
        await Assert.That(TranscriptIds.GeminiRecord(null, line)).IsEqualTo(TranscriptIds.GeminiRecord("n1", line));
    }

    [Test]
    public async Task Copilot_record_is_its_guid_id_else_the_hash_of_the_utf8_line() {
        const string line = "{\"type\":\"user.message\",\"data\":{}}";
        await Assert.That(TranscriptIds.CopilotRecord("b4fbc5e5-3847-423d-8442-acd4bf05a65e", line)).IsEqualTo(Guid.Parse("b4fbc5e5-3847-423d-8442-acd4bf05a65e"));
        await Assert.That(TranscriptIds.CopilotRecord("m1", line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line))));
        await Assert.That(TranscriptIds.CopilotRecord(null, line)).IsEqualTo(TranscriptIds.CopilotRecord("m1", line));
    }

    [Test]
    public async Task Vibe_record_hashes_line_number_space_line() {
        const string line = "{\"role\":\"user\",\"content\":\"yes\"}";
        await Assert.That(TranscriptIds.VibeRecord(7, line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("7 " + line))));
        await Assert.That(TranscriptIds.VibeRecord(7, line)).IsNotEqualTo(TranscriptIds.VibeRecord(9, line));
    }

    [Test]
    public async Task Vibe_tool_call_is_the_sibling_with_a_tool_suffix() {
        await Assert.That(TranscriptIds.VibeToolCall(Primary, "call_1")).IsEqualTo(TranscriptIds.Sibling(Primary, "tool:call_1"));
    }

    [Test]
    public async Task Vibe_entry_hashes_the_prefixed_entry_id() {
        await Assert.That(TranscriptIds.VibeEntry("user-turn-abc-1"))
            .IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("vibe-entry:user-turn-abc-1"))));
        await Assert.That(TranscriptIds.VibeEntry("a")).IsNotEqualTo(TranscriptIds.VibeEntry("b"));
    }

    /// kcap-server's MuseDeterministicIdTests freezes the first; the second is the tool call its v1 Muse baseline stores.
    [Test]
    [Arguments("01a1368b-38a8-7a43-880e-e994ade1ca52", "de01b8ce-ac3c-4087-8219-bfa1203e45c3", "8de7afc5-ba3e-58b8-a93a-cc223aece949")]
    [Arguments("01a13694-b0ad-7ae2-8bf5-2af6357a0847", "c2606efb-7cad-49c1-ad70-554a9d30f4b1", "d001a605-274c-58d3-96f5-df778fa9ae8c")]
    public async Task Muse_record_is_the_servers_v5_uuid(string streamId, string recordId, string expected) {
        var id = TranscriptIds.MuseRecord(streamId, recordId);

        await Assert.That(id.ToString()).IsEqualTo(expected);
        await Assert.That(id.Version).IsEqualTo(5);
        await Assert.That(TranscriptIds.MuseRecord(streamId, "other")).IsNotEqualTo(id);
        await Assert.That(TranscriptIds.MuseRecord("other", recordId)).IsNotEqualTo(id);
    }

    [Test]
    public async Task Muse_tool_result_is_the_record_id_suffixed_with_the_index() {
        await Assert.That(TranscriptIds.MuseToolResult("stream-1", "rec-1", 2)).IsEqualTo(TranscriptIds.MuseRecord("stream-1", "rec-1:2"));
    }

    /// kcap-server's v1 Cursor baseline stores these corpus events under the same ids: a prompt, an assistant text and
    /// a tool call from one line, a tool result, and a subagent's prompt under its scope.
    [Test]
    [Arguments("c0cab74dcfb64bcbbeb8e86ad0ce5281", 0, "user", 0, "UserMessageReceived", "649ca20a-44e5-5303-bb52-67fd91756a17")]
    [Arguments("c0cab74dcfb64bcbbeb8e86ad0ce5281", 1, "assistant_text", 0, "AssistantTextGenerated", "241434f8-47c1-534a-a40e-e36fa686b42c")]
    [Arguments("c0cab74dcfb64bcbbeb8e86ad0ce5281", 1, "tool_use", 1, "AssistantToolCallsGenerated", "0dadedcd-a6cf-5b8b-bbd0-822999f7f80a")]
    [Arguments("cc000000000040008000000000000001", 2, "tool_result", 1, "ToolResultReceived", "8640f40d-48fc-5b3b-b4e9-efa74c1ad1c0")]
    [Arguments("cc000000000040008000000000000001:cc0000000000400080000000000000c1", 0, "user", 0, "UserMessageReceived", "4d4fc94b-78dd-5112-9318-3c82e48fcf08")]
    public async Task Cursor_block_is_the_servers_v5_uuid(string idScope, int lineNumber, string kind, int blockIndex, string eventType, string expected) {
        var id = TranscriptIds.CursorBlock(idScope, lineNumber, kind, blockIndex, eventType);

        await Assert.That(id.ToString()).IsEqualTo(expected);
        await Assert.That(id).IsEqualTo(TranscriptIds.Uuid5(new Guid("8b2e0c3a-b3d4-4f0a-9c0c-c43f59a5d5e1"), $"{idScope}:line:{lineNumber}:{kind}:{blockIndex}:{eventType}"));
    }

    /// kcap-server's KiroDeterministicIdTests freezes these same four, so a Kiro event and a Kiro hook keep one derivation.
    [Test]
    [Arguments("sess-a", "user", "start", "c9c3f93a-ff06-5402-bd3e-0ad79cf30539")]
    [Arguments("sess-a", "user", "stop", "fbc81412-8661-5396-bb2e-02b213754037")]
    [Arguments("11111111-2222-3333-4444-555555555555", "agent", "turn", "183cff1b-eb82-55d0-80f8-7a69912ea3c4")]
    [Arguments("sess-é中:colon", "sc/ope", "kind-4", "6592ad3c-0846-5822-b678-b8c88f38cd3c")]
    public async Task Kiro_record_is_the_servers_v5_uuid(string sessionId, string key, string kind, string expected) {
        var id = TranscriptIds.KiroRecord(sessionId, key, kind);

        await Assert.That(id.ToString()).IsEqualTo(expected);
        await Assert.That(id.Version).IsEqualTo(5);
    }

    [Test]
    public async Task Kiro_fallback_hashes_the_line_then_colon_kind() {
        await Assert.That(TranscriptIds.KiroFallback("{\"kind\":\"Prompt\"}", "user"))
            .IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("{\"kind\":\"Prompt\"}:user"))));
    }

    [Test]
    public async Task Kiro_usage_hashes_the_prefixed_scope_and_anchor() {
        await Assert.That(TranscriptIds.KiroUsage("sess:agent", "msg-1"))
            .IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("kiro-usage:sess:agent:msg-1"))));
    }

    [Test]
    public async Task Grok_ids_hash_the_prefixed_event_id_and_a_line_without_one_goes_by_its_hex_hash() {
        await Assert.That(TranscriptIds.Grok("e-1")).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("grok:e-1"))));
        await Assert.That(TranscriptIds.GrokUsage("e-1", "m")).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("grok:e-1:usage:m"))));
        await Assert.That(TranscriptIds.GrokLineEventId(3, "{}")).IsEqualTo(Convert.ToHexString(XxHash128.Hash(Encoding.UTF8.GetBytes("3\n{}"))));
    }

    [Test]
    public async Task Antigravity_step_hashes_the_utf8_line() {
        await Assert.That(TranscriptIds.AntigravityStep("{\"type\":\"USER_INPUT\"}"))
            .IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("{\"type\":\"USER_INPUT\"}"))));
    }

    [Test]
    public async Task Antigravity_usage_hashes_the_prefixed_scope_and_row() {
        await Assert.That(TranscriptIds.AntigravityUsage("sess:agent", 3))
            .IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("antigravity-usage:sess:agent:3"))));
    }

    [Test]
    public async Task Muse_assistant_item_is_the_v5_uuid_of_scope_turn_and_invariant_seq() {
        var id = TranscriptIds.MuseAssistantItem("sess:agent", "turn-1", -7);

        await Assert.That(id).IsEqualTo(TranscriptIds.Uuid5(new Guid("3f9c2b7e-5d41-4a0b-9e6c-8b2f1d7a4c53"), "sess:agent:turn-1:-7"));
        await Assert.That(id.Version).IsEqualTo(5);
    }

    /// kcap-server's MuseAssistantDeterministicIdTests freezes these same two.
    [Test]
    [Arguments(12L, "5cdf20b6-84fe-514a-94e8-1276ca83a504")]
    [Arguments(13L, "65e68cb3-0ecf-5d64-b182-1294e08bc62c")]
    public async Task Muse_assistant_item_is_the_servers_v5_uuid(long seq, string expected) {
        await Assert.That(TranscriptIds.MuseAssistantItem("a1b2c3d4e5f60718293a4b5c6d7e8f90", "7c1e9d2a-0b3f-4e55-8a61-2f9d0c4b7e10", seq).ToString())
            .IsEqualTo(expected);
    }

    [Test]
    public async Task OpenCode_hashes_the_utf8_key_and_suffixes_a_tool_part_for_its_call_and_result() {
        await Assert.That(TranscriptIds.OpenCode("prt_tool1")).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes("prt_tool1"))));
        await Assert.That(TranscriptIds.OpenCodeToolCall("prt_tool1")).IsEqualTo(TranscriptIds.OpenCode("prt_tool1:call"));
        await Assert.That(TranscriptIds.OpenCodeToolResult("prt_tool1")).IsEqualTo(TranscriptIds.OpenCode("prt_tool1:result"));
    }

    /// Fixed vectors: a later change to any derivation fails here even if the framing tests are edited too.
    [Test]
    [Arguments("sibling", "580a4b24-18bc-bc19-9676-ed805dff4bdd")]
    [Arguments("claude-fallback", "076b76f4-34c5-66d5-20c5-52bcc518f62c")]
    [Arguments("claude-block", "de376eef-af14-5d28-b930-bbbef251635d")]
    // kcap-server's Claude normalizer stores an image of this message, in this session, under the same id.
    [Arguments("claude-attachment", "0b490d55-ee63-9502-52cf-805953334b85")]
    [Arguments("claude-retraction", "af166b98-4df9-03c7-e556-9199ad7f030a")]
    [Arguments("codex-record", "b17e302c-b826-0247-6fda-5634e74c45ce")]
    // kcap-server's CodexNormalizerTests pins the id it stores for this redacted line to the same value.
    [Arguments("codex-redacted-user", "9f1a5a84-cd92-5e9f-72ff-6db814f55976")]
    [Arguments("codex-usage-backfill", "5bccf7a4-6eee-83b2-b58c-f10627871a82")]
    [Arguments("gemini-record-fallback", "fd3d945b-502d-9061-e197-a8bed75d598b")]
    // kcap-server's v1 Gemini baseline stores this record's thinking part under the same id.
    [Arguments("gemini-thinking", "df992b2b-ffcf-9188-4aa2-863991f852a8")]
    // kcap-server's v1 Grok baseline stores its corpus prompt and turn usage under these two ids.
    [Arguments("grok-event", "e4716418-6ac0-af2d-7410-ba5d12a6f87b")]
    [Arguments("grok-usage", "8244c93c-0493-0974-18e2-d60601031101")]
    // kcap-server's Grok normalizer stores this line, which names no event id, under the same id.
    [Arguments("grok-line", "7264b3e6-5c97-9534-ab9a-eef899742b9a")]
    [Arguments("vibe-record", "a14e98cf-4aa1-a507-7ec2-d0e71ed0cdbd")]
    [Arguments("vibe-tool-call", "6509a28d-49aa-5bf6-7f69-110d330e62f4")]
    [Arguments("vibe-entry", "d219bcea-d51a-d28e-6f8a-c4d4635844d0")]
    // kcap-server's v1 Muse baseline stores its corpus prompt and its bash result under these ids.
    [Arguments("muse-record", "e1620d88-3087-57a9-bfd1-e861e88bcff4")]
    [Arguments("muse-tool-result", "32e5e32f-faa6-55a6-9a6e-6ae2240be791")]
    // kcap-server's v1 Kiro baseline stores its corpus prompt under this id.
    [Arguments("kiro-record", "e64903f7-a47d-58e2-89f8-632e007b4463")]
    [Arguments("kiro-fallback", "f90b0ae9-4cfc-9416-d235-73a9332a649b")]
    [Arguments("kiro-usage", "daa9f1c9-0ac4-0c62-afc0-c8890fdecf69")]
    // kcap-server's v1 Muse Assistant baseline stores its corpus prompt under this id.
    [Arguments("muse-assistant-item", "23b0d476-54ea-576b-9f36-a771325e04b7")]
    // The four OpenCode ids from a message, a part and a tool part are the ones kcap-server stored for its corpus.
    [Arguments("opencode-message", "62a73d5f-a531-3bc5-f777-dcfa427bd75f")]
    [Arguments("opencode-part", "f4fadcc6-a417-b1da-6f1b-27d3b0f64760")]
    [Arguments("opencode-tool-call", "22531010-9c94-6693-c170-3ac13e3186b8")]
    [Arguments("opencode-tool-result", "62150ae9-7860-c64a-0f74-fc2feecd1481")]
    [Arguments("opencode-text-key", "adc1e9b5-1b07-7f3e-c779-cfaa5e523818")]
    [Arguments("antigravity-step", "a2cb583b-f936-f782-f8cb-76217f4cb360")]
    // kcap-server's Antigravity normalizer gives this planner's call, and this usage row, the same ids.
    [Arguments("antigravity-call", "fce1d18b-62bb-9031-2a74-08d3c9b9c0a4")]
    [Arguments("antigravity-usage", "45eb26e7-42a6-2bde-4669-22991d56a027")]
    [Arguments("copilot-record-fallback", "f089bd15-3b46-d938-b630-16e63caca1eb")]
    // kcap-server's v1 Copilot baseline stores each of these corpus parts under the same id.
    [Arguments("copilot-thinking", "47b93348-db0a-fc5e-eb2c-984b5919f655")]
    [Arguments("copilot-text", "027e339f-c500-dc3d-bb18-e53caee0288f")]
    [Arguments("copilot-tool", "73989202-72e4-eec1-b53a-d75d9a3d9588")]
    [Arguments("copilot-shutdown-usage", "7b3430c5-79c4-e4a5-aa60-1fd0b690d99b")]
    [Arguments("copilot-session-metrics", "222a87b3-7fea-3eb9-3155-dc935e8a0fec")]
    [Arguments("copilot-attachment", "ae7a9925-a70b-ed33-78a4-5d999b62b92a")]
    public async Task Vectors_are_fixed(string name, string expected) {
        await Assert.That(Vector(name).ToString("D")).IsEqualTo(expected);
    }

    internal static Guid Vector(string name) => name switch {
        "sibling"           => TranscriptIds.Sibling(Primary, "result"),
        "claude-fallback"   => TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}"),
        "claude-block"      => TranscriptIds.ClaudeBlock(Primary, 2),
        "claude-attachment" => TranscriptIds.ClaudeAttachment("5f6c2b1a-9e3d-4a7c-bb12-1c2d3e4f5a6b", Primary, 3),
        "claude-retraction" => TranscriptIds.ClaudeRetraction(Primary),
        "codex-record"      => TranscriptIds.CodexRecord("{\"type\":\"response_item\",\"payload\":{}}"),
        "codex-redacted-user" => TranscriptIds.CodexRecord("""
            {"timestamp":"2026-10-21T02:10:01.207Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"use token [REDACTED:github-token]"}],"internal_chat_message_metadata_passthrough":{"turn_id":"01a15c38-f9fc-7eb3-a679-cc676926d28f","create_time":1792548601.2,"content_item_kinds":["user.text"]}}}
            """),
        "codex-usage-backfill" => TranscriptIds.Sibling(Primary, "usage-backfill"),
        "gemini-record-fallback" => TranscriptIds.GeminiRecord("n1", "{\"id\":\"n1\",\"type\":\"user\"}"),
        "gemini-thinking"   => TranscriptIds.Sibling(TranscriptIds.GeminiRecord("a3b19e5f-275a-407f-891f-9b3661d7e79b", "{}"), "thinking"),
        "grok-event"        => TranscriptIds.Grok("01b00000-0000-7000-8000-000000000001-7"),
        "grok-usage"        => TranscriptIds.GrokUsage("01b00000-0000-7000-8000-000000000001-98", "grok-4.5-build"),
        "grok-line"         => TranscriptIds.Grok(TranscriptIds.GrokLineEventId(4, GrokLine)),
        "vibe-record"       => TranscriptIds.VibeRecord(7, "{\"role\":\"user\",\"content\":\"hi\"}"),
        "vibe-tool-call"    => TranscriptIds.VibeToolCall(Primary, "call_1"),
        "vibe-entry"        => TranscriptIds.VibeEntry("user-turn-abc-1"),
        "muse-record"       => TranscriptIds.MuseRecord("01a13694-b0ad-7ae2-8bf5-2af6357a0847", "38fdee41-9506-4307-bb01-929f5d2e8d0c"),
        "muse-tool-result"  => TranscriptIds.MuseToolResult("01a13694-b0ad-7ae2-8bf5-2af6357a0847", "ccc213d1-33f1-474d-9b73-87b463e34d9e", 0),
        "kiro-record"       => TranscriptIds.KiroRecord("5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b", "9415c9c4-ddb3-4438-9cf9-9df865547069", "user"),
        "kiro-fallback"     => TranscriptIds.KiroFallback("{\"kind\":\"Prompt\"}", "user"),
        "kiro-usage"        => TranscriptIds.KiroUsage("sess:agent", "msg-1"),
        "muse-assistant-item" => TranscriptIds.MuseAssistantItem("66c0235eaa090568f04da9bf3d2675f2", "7c1e9d2a-0b3f-4e55-8a61-2f9d0c4b7e10", 12),
        "opencode-message"     => TranscriptIds.OpenCode("msg_ef5f2a28200171s67y5GODESjO"),
        "opencode-part"        => TranscriptIds.OpenCode("prt_ef5f2a5070014eRszLgUFChcFr"),
        "opencode-tool-call"   => TranscriptIds.OpenCodeToolCall("prt_tool1"),
        "opencode-tool-result" => TranscriptIds.OpenCodeToolResult("prt_tool1"),
        "opencode-text-key"    => TranscriptIds.OpenCode("hello"),
        "antigravity-step"        => TranscriptIds.AntigravityStep(AntigravityPlanner),
        "antigravity-call"        => TranscriptIds.Sibling(TranscriptIds.AntigravityStep(AntigravityPlanner), "call:0"),
        "antigravity-usage"       => TranscriptIds.AntigravityUsage("sess:agent", 3),
        "copilot-record-fallback" => TranscriptIds.CopilotRecord(null, "{\"type\":\"user.message\",\"data\":{}}"),
        "copilot-thinking"        => TranscriptIds.Sibling(Guid.Parse("0d5668ad-cf68-4c8c-a141-a5ad7151d20a"), "thinking"),
        "copilot-text"            => TranscriptIds.Sibling(Guid.Parse("d3f62452-0fd2-432d-9a53-511254635987"), "text"),
        "copilot-tool"            => TranscriptIds.Sibling(Guid.Parse("0d5668ad-cf68-4c8c-a141-a5ad7151d20a"), "tool:toolu_01BxzkztvHBYYJ5V5onD9vHw"),
        "copilot-shutdown-usage"  => TranscriptIds.Sibling(Guid.Parse("60a8d8ea-b6d5-4c8f-a92d-349302e58d8e"), "usage:claude-haiku-4.5"),
        "copilot-session-metrics" => TranscriptIds.Sibling(Guid.Parse("60a8d8ea-b6d5-4c8f-a92d-349302e58d8e"), "session-metrics"),
        "copilot-attachment"      => TranscriptIds.Sibling(Guid.Parse("c35e814a-3cb6-4f25-98b6-f3388b8b67bb"),
            "att:sha256:ca96adb50da6a5f6cbfe03c9c5eb8ad8f1a6c88f526b8a4b9d9d7d3b9ef90157"),
        _                   => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
