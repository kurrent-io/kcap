using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Dsh;

namespace Capacitor.Cli.Tests.Unit.Harness.Dsh;

public class DshImportSourceTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string Header   = """{"$kcap":"header","version":4,"id":"session-e1d79e8a-9b62-4b23-b576-7e7493c09dba","createdAt":1785730000000,"isSeeded":false,"delegationDepth":0,"cwd":"/work"}""";
    const string UserLine = """{"type":"user/message","seq":2,"time":1785730000100,"surfaceOp":"append","data":{"id":"u1","role":"user","source":{"kind":"user"},"content":[{"type":"text","text":"hi"}]}}""";
    const string RawId    = "session-e1d79e8a-9b62-4b23-b576-7e7493c09dba";

    DshImportSource Source => new(Tmp.Path, _ => Task.FromResult<RepositoryPayload?>(null), TimeProvider.System);

    static readonly DiscoveryFilters All = new(null, null, null, 1);

    [Test]
    public async Task Discovery_keys_a_session_by_its_dashless_guid_and_reads_cwd_from_the_header() {
        Tmp.CreateFile($"{RawId}.jsonl", Header + "\n" + UserLine + "\n");

        var found = await Source.DiscoverAsync(All, CancellationToken.None);

        await Assert.That(found.Count).IsEqualTo(1);
        await Assert.That(found[0].SessionId).IsEqualTo("e1d79e8a9b624b23b5767e7493c09dba");
        await Assert.That(found[0].Vendor).IsEqualTo(HarnessId.Dsh);
        await Assert.That(found[0].Cwd).IsEqualTo("/work");
        await Assert.That(found[0].FirstTimestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1785730000000));
    }

    [Test]
    public async Task Discovery_carries_a_subagents_parent_from_the_header() {
        const string child = """{"$kcap":"header","version":4,"id":"session-child","createdAt":1,"isSeeded":true,"delegationDepth":1,"parentSession":"session-parent","origin":"subagent"}""";
        Tmp.CreateFile("session-child.jsonl", child + "\n" + UserLine + "\n");

        var found = await Source.DiscoverAsync(All, CancellationToken.None);

        await Assert.That(found[0].SourceMeta!["ParentSession"]).IsEqualTo("session-parent");
    }

    [Test]
    public async Task A_session_filter_matches_the_raw_or_the_canonical_id() {
        Tmp.CreateFile($"{RawId}.jsonl", Header + "\n" + UserLine + "\n");

        var byRaw       = await Source.DiscoverAsync(new(null, RawId, null, 1), CancellationToken.None);
        var byCanonical = await Source.DiscoverAsync(new(null, "e1d79e8a9b624b23b5767e7493c09dba", null, 1), CancellationToken.None);
        var miss        = await Source.DiscoverAsync(new(null, "nomatch", null, 1), CancellationToken.None);

        await Assert.That(byRaw.Count).IsEqualTo(1);
        await Assert.That(byCanonical.Count).IsEqualTo(1);
        await Assert.That(miss.Count).IsEqualTo(0);
    }

    [Test]
    [Arguments("""{"type":"user/message","data":{"source":{"kind":"user"}}}""", true)]
    [Arguments("""{"type":"user/message","data":{"source":{"kind":"goal"}}}""", false)]
    [Arguments("""{"type":"assistant/message","data":{}}""", true)]
    [Arguments("""{"type":"tool/result","surfaceOp":"append","data":{}}""", true)]
    [Arguments("""{"type":"tool/result","surfaceOp":{"op":"replace","startSeq":1,"endSeq":1},"data":{}}""", false)]
    [Arguments("""{"type":"tool/ptc-dispatch","data":{}}""", true)]
    [Arguments("""{"type":"tool/call","data":{}}""", false)]
    [Arguments("""{"type":"turn/end","data":{}}""", false)]
    [Arguments("""{"$kcap":"header","id":"s"}""", false)]
    [Arguments("""{"$kcap":"disposed","id":"s"}""", false)]
    [Arguments("not json", false)]
    public async Task Only_lines_the_normalizer_projects_advance_the_watermark(string line, bool expected) =>
        await Assert.That(DshImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
}
