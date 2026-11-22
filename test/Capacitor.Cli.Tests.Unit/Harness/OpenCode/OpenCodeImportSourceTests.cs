using System.Text.Json.Nodes;
using System.Web;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.OpenCode;
using Capacitor.Cli.Core.Transcripts;
using Capacitor.Cli.Harness.OpenCode;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Unit.Harness.OpenCode;

public class OpenCodeImportSourceTests {
    [TempHome] public required TempHome Home { get; init; }

    [Test]
    public async Task discovery_returns_roots_with_cwd_and_timestamp_excludes_children() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_root", null, "/work/a", "Root", 1782241513759);
        tmp.AddSession("ses_child", "ses_root", "/work/a", "Child", 1782241513761);
        tmp.AddMessageWithText("ses_root", "msg_1", "hello", 1782241513760);

        var source   = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath, TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        await Assert.That(sessions.Count).IsEqualTo(1);
        await Assert.That(sessions[0].SessionId).IsEqualTo("ses_root");
        await Assert.That(sessions[0].Vendor).IsEqualTo(HarnessId.OpenCode);
        await Assert.That(sessions[0].Cwd).IsEqualTo("/work/a");
        await Assert.That(sessions[0].FirstTimestamp)
            .IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1782241513759));
    }

    [Test]
    public async Task discovery_filters_by_cwd_and_session() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_a", null, "/work/a", "A", 100);
        tmp.AddSession("ses_b", null, "/work/b", "B", 200);
        tmp.AddMessageWithText("ses_a", "m1", "x", 100);
        tmp.AddMessageWithText("ses_b", "m2", "y", 200);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath, TimeProvider.System);

        var byCwd = await source.DiscoverAsync(new DiscoveryFilters("/work/b", null, null, 0), CancellationToken.None);
        await Assert.That(byCwd.Select(s => s.SessionId)).IsEquivalentTo(new[] { "ses_b" });

        var bySession = await source.DiscoverAsync(new DiscoveryFilters(null, "ses_a", null, 0), CancellationToken.None);
        await Assert.That(bySession.Select(s => s.SessionId)).IsEquivalentTo(new[] { "ses_a" });
    }

    [Test]
    public async Task IsAvailable_false_when_db_missing() {
        using var tmp = new TempDir();
        var source = new OpenCodeImportSource(tmp.PathTo("no-such-kcap.db"), tmp.PathTo("ledger.json"), TimeProvider.System);
        await Assert.That(source.IsAvailable).IsFalse();
    }

    /// An unreadable database skips OpenCode and says so on the run's own stderr.
    [Test]
    public async Task an_unreadable_database_is_reported_on_the_runs_stderr() {
        using var tmp    = new TempDir();
        var       db     = tmp.CreateFile("opencode.db", "not a database");
        var       source = new OpenCodeImportSource(db, tmp.PathTo("ledger.json"), TimeProvider.System);
        var       stderr = new StringWriter();

        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0) { Stderr = stderr }, CancellationToken.None);

        await Assert.That(sessions).IsEmpty();
        await Assert.That(stderr.ToString()).Contains("OpenCode discovery skipped");
    }

    /// <summary>A db only OpenCode v2 has written is discovered like a v1 one: its root sessions, with their directory,
    /// title and creation time.</summary>
    [Test]
    public async Task a_v2_database_is_discovered() {
        using var tmp = new OpenCodeDbFixture(v1: false, v2: true);
        tmp.AddV2Session("ses_root", null, "/work/a", "Root", 1795148344953);
        tmp.AddV2Session("ses_child", "ses_root", "/work/a", null, 1795148344999);
        var stderr = new StringWriter();

        var source   = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath, TimeProvider.System);
        var sessions = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0) { Stderr = stderr }, CancellationToken.None);

        var root = sessions.Single();
        await Assert.That(root.SessionId).IsEqualTo("ses_root");
        await Assert.That(root.Cwd).IsEqualTo("/work/a");
        await Assert.That(root.FirstTimestamp).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1795148344953));
        await Assert.That(root.SourceMeta["Title"]).IsEqualTo("Root");
        await Assert.That(stderr.ToString()).IsEmpty();
    }

    [Test]
    public async Task discovery_treats_ms_as_ms_and_seconds_as_seconds() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_ms",  null, "/w", "ms",  1782241513759);
        tmp.AddSession("ses_sec", null, "/w", "sec", 1782241513);
        tmp.AddMessageWithText("ses_ms",  "m1", "x", 1782241513759);
        tmp.AddMessageWithText("ses_sec", "m2", "x", 1782241513);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath, TimeProvider.System);
        var byId = (await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None))
            .ToDictionary(s => s.SessionId, s => s.FirstTimestamp);

        await Assert.That(byId["ses_ms"]).IsEqualTo(DateTimeOffset.FromUnixTimeMilliseconds(1782241513759));
        await Assert.That(byId["ses_sec"]).IsEqualTo(DateTimeOffset.FromUnixTimeSeconds(1782241513));
    }

    [Test]
    public async Task discovery_handles_null_directory_and_excludes_it_under_cwd_filter() {
        using var tmp = new OpenCodeDbFixture();
        tmp.AddSession("ses_nodir", null, dir: null, "No dir", 100);
        tmp.AddMessageWithText("ses_nodir", "m1", "x", 100);

        var source = new OpenCodeImportSource(tmp.DbPath, tmp.LedgerPath, TimeProvider.System);

        var all = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        await Assert.That(all.Single().Cwd).IsNull();

        var filtered = await source.DiscoverAsync(new DiscoveryFilters("/work/a", null, null, 0), CancellationToken.None);
        await Assert.That(filtered.Count).IsEqualTo(0);
    }

    [Test]
    public async Task classify_new_when_server_has_no_watermark() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await Assert.That(classified[0].Vendor).IsEqualTo(HarnessId.OpenCode);
        await Assert.That(classified[0].FilePath).IsEqualTo("");
    }

    [Test]
    public async Task classify_already_loaded_after_a_full_import_records_the_ledger() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(p).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var ctx = new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home);

        // First run records the ledger (with the internally-computed fingerprint) on session-end.
        var s1 = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var c1 = await s1.ClassifyAsync(await s1.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c1[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
        await s1.ImportSessionAsync(c1[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        // Fresh source reloads the ledger → fingerprint matches → AlreadyLoaded.
        var s2 = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var c2 = await s2.ClassifyAsync(await s2.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(c2[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);
    }

    /// <summary>A server that takes canonical batches gets an imported session's transcript, a v2 subagent's included,
    /// on the canonical route, where its v2 messages are read; none of it goes raw.</summary>
    [Test]
    public async Task import_sends_the_transcript_canonical_when_the_server_takes_it() {
        using var fix = new OpenCodeDbFixture(v1: false, v2: true);
        fix.AddV2Session("ses_p", null, "/w", "Parent", 100);
        fix.AddV2Message("ses_p", "msg_1", "user", 1, 101, new JsonObject { ["time"] = new JsonObject { ["created"] = 101 }, ["text"] = "hello" });
        fix.AddV2Session("ses_k", "ses_p", "/w", null, 102);
        fix.AddV2Message("ses_k", "msg_2", "user", 1, 103, new JsonObject { ["time"] = new JsonObject { ["created"] = 103 }, ["text"] = "count" });

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/api/ingest/capabilities").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"canonical_format":1}"""));
        server.Given(Request.Create().WithPath("/hooks/transcript/canonical").UsingPost())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"next_line_number":1}"""));
        foreach (var path in new[] { "/hooks/session-start/opencode", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop",
                                     "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var classified = await source.ClassifyAsync(await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None),
            new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home), CancellationToken.None);
        var outcome = await source.ImportSessionAsync(classified.Single(),
            new ImportContext(client, server.Url!, false) { Canonical = new IngestCapability(client, server.Url!, TimeProvider.System) }, CancellationToken.None);

        var canonical = server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript/canonical").Select(e => e.RequestMessage.Body!).ToList();
        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(canonical.Count).IsEqualTo(2);
        await Assert.That(canonical[0]).Contains("hello");
        await Assert.That(canonical[1]).Contains("count");
        await Assert.That(server.LogEntries.Where(e => e.RequestMessage.Path == "/hooks/transcript")).IsEmpty();
    }

    /// <summary>A server that does not take canonical batches cannot read v2 lines sent raw, so a session holding any is
    /// imported but not recorded complete: the next run, against a server that takes them, imports it again. A v1-only
    /// session sent raw is recorded, as <c>classify_already_loaded_after_a_full_import_records_the_ledger</c> shows.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task a_v2_session_sent_raw_is_not_recorded_complete(bool v2InChild) {
        using var fix = new OpenCodeDbFixture(v1: true, v2: true);
        fix.AddSession("ses_p", null, "/w", "Parent", 100);
        fix.AddMessageWithText("ses_p", "m1", "from v1", 101);
        if (v2InChild) {
            fix.AddV2Session("ses_k", "ses_p", "/w", null, 102);
            fix.AddV2Message("ses_k", "msg_2", "user", 1, 103, new JsonObject { ["time"] = new JsonObject { ["created"] = 103 }, ["text"] = "count" });
        } else {
            fix.AddV2Session("ses_p", null, "/w", "Parent", 100);
            fix.AddV2Message("ses_p", "msg_2", "user", 2, 104, new JsonObject { ["time"] = new JsonObject { ["created"] = 104 }, ["text"] = "from v2" });
        }

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/api/ingest/capabilities").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var path in new[] { "/hooks/session-start/opencode", "/hooks/transcript", "/hooks/subagent-start", "/hooks/subagent-stop",
                                     "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(path).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var ctx    = new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home);
        var import = new ImportContext(client, server.Url!, false) { Canonical = new IngestCapability(client, server.Url!, TimeProvider.System) };

        var first   = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var outcome = await first.ImportSessionAsync(
            (await first.ClassifyAsync(await first.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None)).Single(),
            import, CancellationToken.None);

        var again = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var next  = await again.ClassifyAsync(await again.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), ctx, CancellationToken.None);
        await Assert.That(outcome.Outcome).IsEqualTo(ImportOutcome.Loaded);
        await Assert.That(next.Single().Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    }

    [Test]
    public async Task classify_reimport_bypasses_the_ledger_and_reclassifies() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(p).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();

        var loadCtx = new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home);

        // First run fully imports and records the ledger.
        var s1 = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var c1 = await s1.ClassifyAsync(await s1.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None), loadCtx, CancellationToken.None);
        await s1.ImportSessionAsync(c1[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        var discovered = await new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System)
            .DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);

        // Control: a fresh source with the default (Reimport: false) context still skips it.
        var skip = await new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System)
            .ClassifyAsync(discovered, loadCtx, CancellationToken.None);
        await Assert.That(skip[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.AlreadyLoaded);

        // --reimport bypasses the ledger → the session re-classifies (New here, since the
        // server reports no watermark) and is eligible to re-send.
        var forced = await new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System)
            .ClassifyAsync(discovered, loadCtx with { Reimport = true }, CancellationToken.None);
        await Assert.That(forced[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.New);
    }

    [Test]
    public async Task classify_repair_when_not_in_ledger_and_watermark_present() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"last_line_number":42}"""));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(classified[0].ResumeFromLine).IsEqualTo(42);
    }

    [Test]
    public async Task classify_deleted_when_the_server_answers_410() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddMessageWithText("ses_x", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(410).WithBody("""{"error":"session_deleted"}"""));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.Deleted);
        await Assert.That(classified[0].ProbeErrorReason).IsNull();
    }

    [Test]
    public async Task classify_too_short_for_structural_only_session() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_x", null, "/w", "T", 100);
        fix.AddStructuralMessage("ses_x", "m1", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }

    [Test]
    public async Task classify_too_short_for_zero_message_session() {
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_empty", null, "/w", "Empty", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        using var client = new HttpClient();

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered,
            new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home),
            CancellationToken.None);

        await Assert.That(classified[0].Status).IsEqualTo(ImportCommand.ClassificationStatus.TooShort);
    }

    [Test]
    public async Task import_session_derives_workspace_root_from_cwd_when_a_git_repo_is_present() {
        // Routed imports (OpenCode/Pi/Copilot/Kiro/Antigravity) go straight to ImportSessionAsync,
        // so they must derive workspace_root themselves, as the file-based chain path
        // (ImportChainsAsync) does. Mirrors ImportChainsTests'
        // ImportChainsAsync_derives_workspace_root_from_the_remapped_cwd_not_the_raw_meta_cwd.
        using var fix = new OpenCodeDbFixture();
        var repoDir = Path.Combine(fix.Dir, "repo");
        Directory.CreateDirectory(Path.Combine(repoDir, ".git"));

        fix.AddSession("ses_wr", null, repoDir, "T", 100);
        fix.AddMessageWithText("ses_wr", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(p).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var ctx = new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home);

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, ctx, CancellationToken.None);

        await source.ImportSessionAsync(classified[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        var startEntry = server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/opencode");
        var body        = JsonNode.Parse(startEntry.RequestMessage.Body!)!.AsObject();

        await Assert.That(body["cwd"]?.GetValue<string>()).IsEqualTo(repoDir);
        await Assert.That(body["workspace_root"]?.GetValue<string>()).IsEqualTo(repoDir);
    }

    [Test]
    public async Task import_session_omits_workspace_root_when_cwd_has_no_git_repo() {
        using var fix = new OpenCodeDbFixture();
        var noGitDir = Path.Combine(fix.Dir, "no-git");
        Directory.CreateDirectory(noGitDir);

        fix.AddSession("ses_nowr", null, noGitDir, "T", 100);
        fix.AddMessageWithText("ses_nowr", "m1", "hello", 100);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        foreach (var p in new[] { "/hooks/session-start/opencode", "/hooks/transcript",
                                  "/hooks/set-title", "/hooks/session-end/opencode" })
            server.Given(Request.Create().WithPath(p).UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var ctx = new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home);

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, ctx, CancellationToken.None);

        await source.ImportSessionAsync(classified[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        var startEntry = server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/opencode");
        var body        = JsonNode.Parse(startEntry.RequestMessage.Body!)!.AsObject();

        await Assert.That(body["workspace_root"]).IsNull();
    }

    /// <summary>A child id is whatever OpenCode's database holds, so a reserved character in it must
    /// reach the descendant watermark probe as data, not as query syntax.</summary>
    [Test]
    public async Task import_session_probes_a_descendant_watermark_with_its_exact_agent_id() {
        const string childId = "ses_a&b #c+d";
        using var fix = new OpenCodeDbFixture();
        fix.AddSession("ses_root", null, fix.Dir, "T", 100);
        fix.AddMessageWithText("ses_root", "m1", "hello", 100);
        fix.AddSession(childId, "ses_root", fix.Dir, "Child", 110);
        fix.AddMessageWithText(childId, "m2", "child hello", 110);

        using var server = WireMockServer.Start();
        server.Given(Request.Create().WithPath("/api/sessions/*/last-line").UsingGet())
              .RespondWith(Response.Create().WithStatusCode(404));
        server.Given(Request.Create().WithPath("/hooks/*").UsingPost()).RespondWith(Response.Create().WithStatusCode(200));
        using var client = new HttpClient();
        var ctx = new ClassifyContext(client, server.Url!, MinLines: 1, Home: Home);

        var source     = new OpenCodeImportSource(fix.DbPath, fix.LedgerPath, TimeProvider.System);
        var discovered = await source.DiscoverAsync(new DiscoveryFilters(null, null, null, 0), CancellationToken.None);
        var classified = await source.ClassifyAsync(discovered, ctx, CancellationToken.None);

        await source.ImportSessionAsync(classified[0], new ImportContext(client, server.Url!, false), CancellationToken.None);

        var probedAgentIds = server.LogEntries
            .Where(e => e.RequestMessage.Path.EndsWith("/last-line", StringComparison.Ordinal) && !string.IsNullOrEmpty(e.RequestMessage.RawQuery))
            .SelectMany(e => HttpUtility.ParseQueryString(e.RequestMessage.RawQuery).GetValues("agentId") ?? [])
            .ToArray();

        await Assert.That(probedAgentIds).IsEquivalentTo(new[] { OpenCodeSubagentDiscovery.CanonicalAgentId(childId) });
    }
}
