using System.IO.Hashing;
using System.Text;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Pi;

/// <summary>Pi's ids are a persistence contract: kcap-server stored its Pi sessions under them and dedups by them,
/// so a different derivation would duplicate every event on re-import.</summary>
public class PiIdsTests {
    /// <summary>RFC 4122's own name-based vector: the DNS namespace and <c>www.example.com</c>.</summary>
    [Test]
    public async Task Uuid5_reproduces_the_rfc_vector() =>
        await Assert.That(TranscriptIds.Uuid5(Guid.Parse("6ba7b810-9dad-11d1-80b4-00c04fd430c8"), "www.example.com"))
            .IsEqualTo(Guid.Parse("2ed6657d-e927-568b-95e1-2665a8aea6a2"));

    /// <summary>The vectors kcap-server's <c>PiDeterministicId</c> freezes.</summary>
    [Test]
    [Arguments("sess-a", "user", "start", "bf5e8eb0-48f4-57c8-87cb-0ec50082f59d")]
    [Arguments("sess-a", "user", "stop", "f8710956-00c2-5734-a6b2-d386545dac6d")]
    [Arguments("11111111-2222-3333-4444-555555555555", "agent", "turn", "9e6d1404-41d0-5da1-8ebf-e1dd0e7e5d7c")]
    [Arguments("sess-é中:colon", "sc/ope", "kind-4", "6622d504-209b-5e72-ad82-ffdb129749af")]
    public async Task An_entry_id_matches_the_servers_frozen_vectors(string session, string scope, string kind, string expected) =>
        await Assert.That(TranscriptIds.PiEntry(session, scope, kind).ToString()).IsEqualTo(expected);

    /// <summary>Ids kcap-server's <c>v1</c> baseline holds for the corpus sessions under <c>Fixtures/</c>: the header
    /// keyed by the id it names, every other entry by the stream's canonical session id, and an assistant block by
    /// its index and, for a tool call, its call id.</summary>
    [Test]
    [Arguments("019ed892-0000-7000-8000-000000000001", "session", "SessionStarted", "3899849e-e617-557e-93a7-0e84b38d81f8")]
    [Arguments("019ed892000070008000000000000001", "uwrite01", "user", "3f0c6deb-4c91-54fa-bbec-1d3abe065f5e")]
    [Arguments("019ed892000070008000000000000001", "awrite02", "assistant:0:tool:call_write_1", "2467b01d-4a2d-5d7c-8ee1-2abb1ba925a6")]
    [Arguments("019ed892000070008000000000000001", "rwrite03", "toolResult", "0cfd3368-7ac0-57f9-b6f1-894481addd7d")]
    [Arguments("019ed892000070008000000000000001", "bsum0008", "branch_summary", "21c960d9-519f-5109-b257-3df12c876559")]
    [Arguments("019ed892000070008000000000000001", "comp0012", "compaction", "095e699a-d682-57cf-bf0c-26e5ad5d2dcc")]
    [Arguments("019ed7364cb37d79b2a3a6f858b5328f", "de662abd", "assistant:0:text", "8cf6cddb-dc5b-5bfb-887d-86c26b3bf771")]
    [Arguments("019ed7364cb37d79b2a3a6f858b5328f", "de662abd", "assistant:1:tool:toolu_01A5GsVsGNzSRD5un7GYTNNx", "7623c6db-e232-5c67-a9cc-f7f5b4356a9c")]
    [Arguments("019ed7364cb37d79b2a3a6f858b5328f", "de662abd", "assistant:2:tool:toolu_01TiNb482nH4aq49e9oAVf7g", "01c64ca5-5d72-5036-af38-713c653cca43")]
    [Arguments("019ed7364cb37d79b2a3a6f858b5328f", "5de1ae04", "assistant:0:thinking", "c397681f-93e8-5917-9100-0e26092fca2d")]
    public async Task An_entry_id_matches_what_the_server_stored(string session, string scope, string kind, string expected) =>
        await Assert.That(TranscriptIds.PiEntry(session, scope, kind).ToString()).IsEqualTo(expected);

    [Test]
    public async Task A_line_id_hashes_the_line_then_a_colon_and_the_kind() {
        const string line = """{"type":"message","message":{"role":"user","content":"hi"}}""";

        await Assert.That(TranscriptIds.PiLine(line, "user")).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line + ":user"))));
        await Assert.That(TranscriptIds.PiHeaderLine(line)).IsEqualTo(new Guid(XxHash128.Hash(Encoding.UTF8.GetBytes(line))));
    }
}
