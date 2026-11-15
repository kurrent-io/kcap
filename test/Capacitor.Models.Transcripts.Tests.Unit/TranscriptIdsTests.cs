using System.Buffers.Binary;
using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts.Tests.Unit;

/// Each derivation is a persistence contract: the server dedups by these ids, so the bytes hashed
/// are pinned here twice — once by framing (the exact byte layout) and once by fixed vectors.
public class TranscriptIdsTests {
    static readonly Guid Primary = Guid.Parse("0f1e2d3c-4b5a-6978-8796-a5b4c3d2e1f0");

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

    [Test]
    public async Task Claude_attachment_hashes_scope_then_record_guid_then_little_endian_index() {
        var scope = Encoding.UTF8.GetBytes("sess:agent");
        var expectedInput = new byte[scope.Length + 20];
        scope.CopyTo(expectedInput, 0);
        Primary.TryWriteBytes(expectedInput.AsSpan(scope.Length));
        BinaryPrimitives.WriteInt32LittleEndian(expectedInput.AsSpan(scope.Length + 16), 3);

        await Assert.That(TranscriptIds.ClaudeAttachment("sess:agent", Primary, 3)).IsEqualTo(new Guid(XxHash128.Hash(expectedInput)));
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

    [Test]
    public async Task Muse_record_hashes_stream_and_record_id() {
        const string stream = "01a13694-b0ad-7ae2-8bf5-2af6357a0847";
        const string record = "e6b35c82-47c0-48f2-868a-0e369eccecc8";
        await Assert.That(TranscriptIds.MuseRecord(stream, record))
            .IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes($"muse-record:{stream}:{record}"))));
        await Assert.That(TranscriptIds.MuseRecord(stream, record)).IsNotEqualTo(TranscriptIds.MuseRecord(stream, "other"));
        await Assert.That(TranscriptIds.MuseRecord(stream, record)).IsNotEqualTo(TranscriptIds.MuseRecord("other", record));
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
    [Arguments("claude-attachment", "c0331d1c-5d1f-a123-49d3-820902c4d48b")]
    [Arguments("claude-retraction", "af166b98-4df9-03c7-e556-9199ad7f030a")]
    [Arguments("codex-record", "b17e302c-b826-0247-6fda-5634e74c45ce")]
    // kcap-server's CodexNormalizerTests pins the id it stores for this redacted line to the same value.
    [Arguments("codex-redacted-user", "9f1a5a84-cd92-5e9f-72ff-6db814f55976")]
    [Arguments("codex-usage-backfill", "5bccf7a4-6eee-83b2-b58c-f10627871a82")]
    [Arguments("gemini-record-fallback", "fd3d945b-502d-9061-e197-a8bed75d598b")]
    // kcap-server's v1 Gemini baseline stores this record's thinking part under the same id.
    [Arguments("gemini-thinking", "df992b2b-ffcf-9188-4aa2-863991f852a8")]
    [Arguments("vibe-record", "a14e98cf-4aa1-a507-7ec2-d0e71ed0cdbd")]
    [Arguments("vibe-tool-call", "6509a28d-49aa-5bf6-7f69-110d330e62f4")]
    [Arguments("vibe-entry", "d219bcea-d51a-d28e-6f8a-c4d4635844d0")]
    [Arguments("muse-record", "d8a1a66a-0ef6-88fa-a0ea-41440eb74617")]
    // kcap-server's v1 Kiro baseline stores its corpus prompt under this id.
    [Arguments("kiro-record", "e64903f7-a47d-58e2-89f8-632e007b4463")]
    [Arguments("kiro-fallback", "f90b0ae9-4cfc-9416-d235-73a9332a649b")]
    [Arguments("kiro-usage", "daa9f1c9-0ac4-0c62-afc0-c8890fdecf69")]
    // The four OpenCode ids from a message, a part and a tool part are the ones kcap-server stored for its corpus.
    [Arguments("opencode-message", "62a73d5f-a531-3bc5-f777-dcfa427bd75f")]
    [Arguments("opencode-part", "f4fadcc6-a417-b1da-6f1b-27d3b0f64760")]
    [Arguments("opencode-tool-call", "22531010-9c94-6693-c170-3ac13e3186b8")]
    [Arguments("opencode-tool-result", "62150ae9-7860-c64a-0f74-fc2feecd1481")]
    [Arguments("opencode-text-key", "adc1e9b5-1b07-7f3e-c779-cfaa5e523818")]
    public async Task Vectors_are_fixed(string name, string expected) {
        await Assert.That(Vector(name).ToString("D")).IsEqualTo(expected);
    }

    internal static Guid Vector(string name) => name switch {
        "sibling"           => TranscriptIds.Sibling(Primary, "result"),
        "claude-fallback"   => TranscriptIds.ClaudeFallback(12, "{\"type\":\"user\"}"),
        "claude-block"      => TranscriptIds.ClaudeBlock(Primary, 2),
        "claude-attachment" => TranscriptIds.ClaudeAttachment("sess:agent", Primary, 3),
        "claude-retraction" => TranscriptIds.ClaudeRetraction(Primary),
        "codex-record"      => TranscriptIds.CodexRecord("{\"type\":\"response_item\",\"payload\":{}}"),
        "codex-redacted-user" => TranscriptIds.CodexRecord("""
            {"timestamp":"2026-10-21T02:10:01.207Z","type":"response_item","payload":{"type":"message","role":"user","content":[{"type":"input_text","text":"use token [REDACTED:github-token]"}],"internal_chat_message_metadata_passthrough":{"turn_id":"01a15c38-f9fc-7eb3-a679-cc676926d28f","create_time":1792548601.2,"content_item_kinds":["user.text"]}}}
            """),
        "codex-usage-backfill" => TranscriptIds.Sibling(Primary, "usage-backfill"),
        "gemini-record-fallback" => TranscriptIds.GeminiRecord("n1", "{\"id\":\"n1\",\"type\":\"user\"}"),
        "gemini-thinking"   => TranscriptIds.Sibling(TranscriptIds.GeminiRecord("a3b19e5f-275a-407f-891f-9b3661d7e79b", "{}"), "thinking"),
        "vibe-record"       => TranscriptIds.VibeRecord(7, "{\"role\":\"user\",\"content\":\"hi\"}"),
        "vibe-tool-call"    => TranscriptIds.VibeToolCall(Primary, "call_1"),
        "vibe-entry"        => TranscriptIds.VibeEntry("user-turn-abc-1"),
        "muse-record"       => TranscriptIds.MuseRecord("01a13694-b0ad-7ae2-8bf5-2af6357a0847", "e6b35c82-47c0-48f2-868a-0e369eccecc8"),
        "kiro-record"       => TranscriptIds.KiroRecord("5f6c2b1a9e3d4a7cbb121c2d3e4f5a6b", "9415c9c4-ddb3-4438-9cf9-9df865547069", "user"),
        "kiro-fallback"     => TranscriptIds.KiroFallback("{\"kind\":\"Prompt\"}", "user"),
        "kiro-usage"        => TranscriptIds.KiroUsage("sess:agent", "msg-1"),
        "opencode-message"     => TranscriptIds.OpenCode("msg_ef5f2a28200171s67y5GODESjO"),
        "opencode-part"        => TranscriptIds.OpenCode("prt_ef5f2a5070014eRszLgUFChcFr"),
        "opencode-tool-call"   => TranscriptIds.OpenCodeToolCall("prt_tool1"),
        "opencode-tool-result" => TranscriptIds.OpenCodeToolResult("prt_tool1"),
        "opencode-text-key"    => TranscriptIds.OpenCode("hello"),
        _                   => throw new ArgumentOutOfRangeException(nameof(name)),
    };
}
