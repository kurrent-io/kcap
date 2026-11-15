using System.Globalization;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Harness.Pi;
using Capacitor.Cli.Recording.PrDetection;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.Pi;

/// <summary>
/// Discovery-phase + import-relevance tests for <see cref="PiImportSource"/>
/// against fake <c>~/.pi/agent/sessions</c> trees. Pi stores one
/// tree-structured JSONL per session; discovery reads the <c>session</c> header
/// line for the id/cwd/timestamp and walks the tree recursively.
/// </summary>
public class PiImportSourceTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Sid1 = "11111111-2222-3333-4444-555555555555";
    const string Sid2 = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";

    static void WriteSession(string dir, string uuid, string cwd, string timestamp = "2026-06-12T10:00:00.000Z") {
        Directory.CreateDirectory(dir);
        var lines = new[] {
            $$"""{"type":"session","version":3,"id":"{{uuid}}","timestamp":"{{timestamp}}","cwd":"{{cwd}}"}""",
            """{"type":"message","id":"a1","parentId":null,"timestamp":"2026-06-12T10:00:01.000Z","message":{"role":"user","content":"hello"}}"""
        };
        File.WriteAllLines(Path.Combine(dir, uuid + ".jsonl"), lines);
    }

    [Test]
    public async Task discovery_finds_pi_session_from_header() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");

        var source   = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
        await Assert.That(sessions[0].Vendor).IsEqualTo(HarnessId.Pi);
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp).IsEqualTo(DateTimeOffset.Parse("2026-06-12T10:00:00.000Z", CultureInfo.InvariantCulture));
    }

    [Test]
    public async Task discovery_walks_nested_cwd_subdirs() {
        using var tmp = new TempDir();
        WriteSession(tmp.PathTo("proj-a"), Sid1, cwd: "/work/a");
        WriteSession(tmp.PathTo("proj-b"), Sid2, cwd: "/work/b");

        var source   = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(2);
    }

    [Test]
    public async Task discovery_skips_non_pi_jsonl() {
        using var tmp = new TempDir();
        // A .jsonl whose first line is not a Pi session header.
        tmp.CreateFile("other.jsonl", "{\"type\":\"something\",\"x\":1}\n");

        var source   = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task discovery_skips_session_with_non_guid_header_id() {
        using var tmp = new TempDir();
        // Well-formed {"type":"session"} header but a corrupt, non-GUID id, in a
        // file whose name also yields no uuid. Discovery must skip it rather than
        // minting an arbitrary non-GUID session id — mirrors the live hook path
        // (PiHookCommand.ExtractSessionId rejects non-GUID headers/filenames).
        tmp.CreateFile("corrupt.jsonl", new[] {
            """{"type":"session","version":3,"id":"not-a-guid","timestamp":"2026-06-12T10:00:00.000Z","cwd":"/work/x"}""",
            """{"type":"message","id":"a1","parentId":null,"message":{"role":"user","content":"hello"}}"""
        });

        var source   = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(0);
    }

    [Test]
    public async Task discovery_recovers_session_id_from_filename_when_header_id_missing() {
        using var tmp = new TempDir();
        // Header without an id, but the file is named "<timestamp>_<uuid>.jsonl"
        // (Pi's on-disk convention). Discovery falls back to the filename uuid,
        // the same recovery the live hook path uses for an unflushed header.
        tmp.CreateFile("2026-06-12T10-00-00_" + Sid1 + ".jsonl",
            new[] {
                """{"type":"session","version":3,"timestamp":"2026-06-12T10:00:00.000Z","cwd":"/work/a"}""",
                """{"type":"message","id":"a1","parentId":null,"message":{"role":"user","content":"hi"}}"""
            });

        var source   = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));
    }

    [Test]
    public async Task discovery_applies_session_and_cwd_filters() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");
        WriteSession(tmp.Path, Sid2, cwd: "/work/b");

        var source = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);

        var bySession = await source.DiscoverAsync(new DiscoveryFilters(null, Sid1, null, 0), CancellationToken.None);
        await Assert.That(bySession.Count).IsEqualTo(1);
        await Assert.That(bySession[0].SessionId).IsEqualTo(Sid1.Replace("-", ""));

        var byCwd = await source.DiscoverAsync(new DiscoveryFilters("/work/b", null, null, 0), CancellationToken.None);
        await Assert.That(byCwd.Count).IsEqualTo(1);
        await Assert.That(byCwd[0].SessionId).IsEqualTo(Sid2.Replace("-", ""));
    }

    [Test]
    public async Task is_available_false_when_dir_missing() {
        using var tmp = new TempDir();
        var source = new PiImportSource(Config.Root, tmp.PathTo("nope"), router: new GitProviderRouter(), time: TimeProvider.System);
        await Assert.That(source.IsAvailable).IsFalse();
    }

    [Test]
    public async Task does_not_support_title_generation() {
        // Pi is a routed source (FilePath=""), so it never reaches the chain
        // title worker. Like Copilot/Cursor it relies on the server-side fallback
        // title; advertising true would be a no-op contract lie.
        var source = new PiImportSource(Config.Root, "/nonexistent", router: new GitProviderRouter(), time: TimeProvider.System);
        await Assert.That(source.SupportsTitleGeneration).IsFalse();
    }

    // Mirrors the server PiTranscriptNormalizer's emit/skip set — keep in sync.
    [Test]
    [Arguments("""{"type":"session","id":"x","cwd":"/w"}""", true)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"user","content":"hi"}}""", true)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"assistant","content":[{"type":"text","text":"ok"}]}}""", true)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"toolResult","toolCallId":"c1","content":[]}}""", true)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"bashExecution","command":"ls"}}""", true)]
    [Arguments("""{"type":"model_change","id":"a","modelId":"gpt-5"}""", false)]
    [Arguments("""{"type":"compaction","id":"a","summary":"x"}""", true)]  // compaction → ContextCompacted
    [Arguments("""{"type":"branch_summary","id":"a","summary":"branch","fromId":"b"}""", true)] // branch_summary → AssistantTextGenerated
    [Arguments("""{"type":"branch_summary","id":"a","summary":""}""", false)]
    [Arguments("""{"type":"branch_summary","id":"a","summary":"   "}""", false)]
    [Arguments("""{"type":"label","id":"a","label":"x"}""", false)]
    [Arguments("""{"type":"session_info","id":"a","name":"x"}""", false)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"toolResult","content":[]}}""", false)]
    [Arguments("not valid json", false)]
    // Content-gated to match PiTranscriptNormalizer's emit condition (not just role):
    // assistant with only unsupported/empty blocks, or an empty/contentless user, emit nothing.
    [Arguments("""{"type":"message","id":"a","message":{"role":"assistant","content":[{"type":"image","data":"x"}]}}""", false)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"assistant","content":[]}}""", false)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"assistant","content":[{"type":"toolCall","id":"c1","name":"bash"}]}}""", true)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"user","content":[{"type":"image","data":"x"}]}}""", false)]
    [Arguments("""{"type":"message","id":"a","message":{"role":"user","content":""}}""", false)]
    public async Task is_import_relevant_line_matches_normalizer(string line, bool expected) {
        await Assert.That(PiImportSource.IsImportRelevantLine(line)).IsEqualTo(expected);
    }

    /// <summary>The lifecycle bodies go to the server as written, so their bytes, property order included, are pinned.</summary>
    [Test]
    public async Task import_posts_lifecycle_payloads_byte_for_byte() {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var source     = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, server.Url!, MinLines: 1, Home: new(tmp.Path)), CancellationToken.None);
        await source.ImportSessionAsync(classified[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        string Body(string path) => server.LogEntries.Single(e => e.RequestMessage.Path == path).RequestMessage.Body!;

        await Assert.That(server.LogEntries.Count(e => e.RequestMessage.Path == $"/api/sessions/{Sid1.Replace("-", "")}/last-line")).IsEqualTo(1);
        await Assert.That(Body("/hooks/session-start/pi")).IsEqualTo(
            """{"hook_event_name":"sessionStart","session_id":"11111111222233334444555555555555","source":"startup","cwd":"/work/a","started_at":"2026-06-12T10:00:00.0000000\u002B00:00","origin":"historical-import"}""");
        await Assert.That(Body("/hooks/session-end/pi")).IsEqualTo(
            """{"hook_event_name":"sessionEnd","session_id":"11111111222233334444555555555555","reason":"pi-import","cwd":"/work/a","ended_at":"2026-06-12T10:00:01.0000000\u002B00:00","origin":"historical-import"}""");
    }

    [Test]
    [Arguments("""{"last_line_number":0}""", "Partial", 1)]
    [Arguments("""{"last_line_number":1}""", "AlreadyLoaded", 0)]
    [Arguments("{}", "New", 0)]
    public async Task classify_compares_the_watermark_with_the_last_relevant_line(string watermark, string status, int resume) {
        using var tmp = new TempDir();
        WriteSession(tmp.Path, Sid1, cwd: "/work/a");

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet()).RespondWith(Response.Create().WithStatusCode(200).WithBody(watermark));
        using var client = new HttpClient();

        var source     = new PiImportSource(Config.Root, tmp.Path, router: new GitProviderRouter(), time: TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, new ClassifyContext(client, server.Url!, MinLines: 1, Home: new(tmp.Path)), CancellationToken.None);

        await Assert.That(classified[0].Status.ToString()).IsEqualTo(status);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(resume);
        await Assert.That(classified[0].TotalLines).IsEqualTo(2);
    }
}
