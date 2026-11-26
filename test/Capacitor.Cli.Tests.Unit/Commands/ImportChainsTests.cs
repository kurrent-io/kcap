using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Transcripts;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class ImportChainsTests : IDisposable {
    [TempHome] public required TempHome Home { get; init; }

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    // These tests exercise chaining and repo resolution, not profile selection.
    ImportCommand Import() =>
        new(Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.Under(Home), new FixedCapacitorHttpClient(), router: new GitProviderRouter(), time: TimeProvider.System, console: new TestImportTerminal().Console);
    readonly WireMockServer _server  = WireMockServer.Start();
    // TUnit creates a new class instance per test, so _tempDir is always unique.
    readonly TempDir _tmp     = new();
    readonly string  _tempDir;
    public ImportChainsTests() => _tempDir = _tmp.Path;

    public void Dispose() {
        _server.Stop();
        _tmp.Dispose();
    }

    void StubAllHookEndpoints(int transcriptPriority = 0) {
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .AtPriority(transcriptPriority).RespondWith(Response.Create().WithStatusCode(200));
        // Match both the legacy un-vendored path and the new vendor-routed
        // path the importer uses now (/hooks/session-start/{vendor}) so codex
        // imports can't silently land as claude-tagged SessionStarted events.
        _server.Given(Request.Create().WithPath("/hooks/session-start*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/session-end*").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("{}"));
        _server.Given(Request.Create().WithPath("/hooks/subagent-start").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        _server.Given(Request.Create().WithPath("/hooks/subagent-stop").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
    }

    ImportCommand.SessionClassification MakeNew(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new() {
            SessionId  = id,
            FilePath   = path,
            EncodedCwd = "-tmp-proj",
            Meta       = new() { Cwd = "/tmp/proj" },
            Status     = ImportCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    // No-git variant: both Meta.Cwd AND EncodedCwd must be empty so
    // ImportSingleSessionAsync resolves cwd to null and skips DetectRepositoryAsync.
    // A non-empty EncodedCwd would still decode to a valid cwd, triggering repo
    // detection and polluting parallelism-timing measurements.
    ImportCommand.SessionClassification MakeNewNoGit(string id, int lines) {
        var path = Path.Combine(_tempDir, $"{id}.jsonl");
        File.WriteAllLines(path, Enumerable.Range(0, lines).Select(i =>
            $$$"""{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-{{{i}}}"}}"""
        ));
        return new() {
            SessionId  = id,
            FilePath   = path,
            EncodedCwd = "",                      // DecodeCwdFromDirName returns null on empty input
            Meta       = new SessionMetadata(),   // Cwd = null
            Status     = ImportCommand.ClassificationStatus.New,
            TotalLines = lines,
        };
    }

    [Test]
    public async Task ImportChainsAsync_counts_loaded_sessions() {
        StubAllHookEndpoints();

        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeNew("cnt-s1", 50) },
            new() { MakeNew("cnt-s2", 50) },
            new() { MakeNew("cnt-s3", 50) },
        };

        var completedLines = new ConcurrentBag<string>();
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => completedLines.Add($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var result = await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        await Assert.That(result.Loaded).IsEqualTo(3);
        await Assert.That(result.Errored).IsEqualTo(0);
        await Assert.That(completedLines.Count).IsEqualTo(3);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(false, true)]
    [Arguments(true, true)]
    public async Task Partial_import_retries_a_refused_subagent_before_finalizing_the_parent(bool canonical, bool lateSidecar) {
        const string agentId = "bbbbbbbbbbbbbbbb";
        var path = canonical ? CanonicalRoute.HttpPath : TranscriptPost.RawPath;
        var lineCount = lateSidecar ? 104 : 5;
        var session = MakeNew("resume-child", lineCount);
        var lines = await File.ReadAllLinesAsync(session.FilePath);
        var agents = _tmp.CreateDir(session.SessionId, "subagents");
        var sidecarPath = agents.PathTo($"agent-{agentId}.meta.json");
        if (lateSidecar) {
            // The parent names the call, but only the sidecar can identify the child it spawned.
            lines[2] = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"call","name":"Agent","input":{}}]}}""";
            await File.WriteAllTextAsync(sidecarPath, "{");
        } else {
            lines[3] = $$$"""{"type":"progress","data":{"type":"agent_progress","agentId":"{{{agentId}}}"}}""";
        }
        await File.WriteAllLinesAsync(session.FilePath, lines);
        var agentPath = agents.PathTo($"agent-{agentId}.jsonl");
        await File.WriteAllLinesAsync(agentPath, [
            """{"type":"user","uuid":"8ae2506b-7f4c-4e18-bfb3-ac5b77f472fc","timestamp":"2026-03-15T10:00:00Z","message":{"role":"user","content":"child"}}""",
        ]);

        StubTranscriptRoutes();
        // A canonical refusal can fall back to raw, so refuse both representations of the child's batch.
        foreach (var route in new[] { TranscriptPost.RawPath, CanonicalRoute.HttpPath }) {
            _server.Given(Request.Create().WithPath(route).UsingPost()
                    .WithBody(new JsonPartialMatcher($$"""{"agent_id":"{{agentId}}"}""")))
                .AtPriority(1).RespondWith(Response.Create().WithStatusCode(400));
        }

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var failed = await Import().ImportChainsAsync(client, _server.Url!, [[session with {
            Status = ImportCommand.ClassificationStatus.Partial, ResumeFromLine = 2,
        }]], events, CancellationToken.None);

        await Assert.That(failed.Errored).IsEqualTo(1);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path.StartsWith("/hooks/session-end"))).IsFalse();
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Path.EndsWith("/last-line"))).IsFalse();
        var parent = _server.LogEntries.Where(e => e.RequestMessage.Path == path)
            .Select(e => JsonNode.Parse(e.RequestMessage.Body!)!).Single(b => b["agent_id"] is null);
        var watermark = canonical
            ? parent["lines"]!.AsArray().Last()!["number"]!.GetValue<int>()
            : parent["line_numbers"]!.AsArray().Last()!.GetValue<int>();
        await Assert.That(watermark).IsEqualTo(lateSidecar ? 101 : 2);

        if (lateSidecar) await File.WriteAllTextAsync(sidecarPath, """{"toolUseId":"call"}""");
        _server.ResetMappings();
        _server.ResetLogEntries();
        StubTranscriptRoutes();
        StubWatermark(watermark, priority: 2);
        var retry = (await TranscriptFileClassification.ClassifyAsync(
            new GitProviderRouter(), Config.Root, Home, client, TimeProvider.System, _server.Url!,
            [(session.SessionId, session.FilePath, session.EncodedCwd)], minLines: 1, CancellationToken.None)).Single();
        await Assert.That(retry.Status).IsEqualTo(ImportCommand.ClassificationStatus.Partial);
        await Assert.That(retry.ResumeFromLine).IsEqualTo(watermark + 1);

        StubWatermark(lineCount - 1, priority: 1);
        var completed = await Import().ImportChainsAsync(client, _server.Url!, [[retry]], events, CancellationToken.None);
        await Assert.That(completed.Resumed).IsEqualTo(1);
        var postPaths = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").Select(e => e.RequestMessage.Path);
        await Assert.That(string.Join(" ", postPaths))
            .IsEqualTo($"/hooks/subagent-start {path} /hooks/subagent-stop {path} /hooks/session-end/claude");
        await Assert.That(TranscriptImportLedger.Load(Config.Root).ResumeFrom(_server.Url!, session.SessionId, lineCount - 1)).IsEqualTo(lineCount);
        if (lateSidecar) {
            var start = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/subagent-start");
            await Assert.That(JsonNode.Parse(start.RequestMessage.Body!)?["spawn_call_id"]?.GetValue<string>()).IsEqualTo("call");
        }

        return;

        void StubTranscriptRoutes() {
            StubAllHookEndpoints(transcriptPriority: 2);
            if (!canonical) return;
            _server.Given(Request.Create().WithPath("/api/ingest/capabilities").UsingGet())
                .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json")
                    .WithBody("""{"canonical_format":1}"""));
            _server.Given(Request.Create().WithPath(CanonicalRoute.HttpPath).UsingPost())
                .AtPriority(2).RespondWith(Response.Create().WithStatusCode(200));
        }

        void StubWatermark(int line, int priority) =>
            _server.Given(Request.Create().WithPath($"/api/sessions/{session.SessionId}/last-line").UsingGet())
                .AtPriority(priority).RespondWith(Response.Create().WithStatusCode(200).WithBody($$"""{"last_line_number":{{line}}}"""));
    }

    [Test]
    public async Task Partial_import_waits_to_finalize_until_its_only_unsent_line_is_complete() {
        StubAllHookEndpoints();
        var session = MakeNew("incomplete", 3) with {
            Status = ImportCommand.ClassificationStatus.Partial,
            ResumeFromLine = 2,
        };
        var lines = await File.ReadAllLinesAsync(session.FilePath);
        await File.WriteAllTextAsync(session.FilePath, string.Join("\n", lines));
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var deferred = await Import().ImportChainsAsync(client, _server.Url!, [[session]], events, CancellationToken.None);
        await Assert.That(deferred.Resumed).IsEqualTo(0);
        await Assert.That(_server.LogEntries.Any(e => e.RequestMessage.Method == "POST")).IsFalse();

        await File.AppendAllTextAsync(session.FilePath, "\n");
        var resumed = await Import().ImportChainsAsync(client, _server.Url!, [[session]], events, CancellationToken.None);
        await Assert.That(resumed.Resumed).IsEqualTo(1);
        var end = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-end/claude");
        var body = JsonNode.Parse(end.RequestMessage.Body!)!;
        await Assert.That(body["ended_at"]?.GetValue<string>()).IsEqualTo("2026-03-15T10:00:00.0000000+00:00");
    }

    [Test]
    public async Task ImportChainsAsync_reports_per_session_progress() {
        // Regression: per-session slot rows always showed 0% because
        // BatchFlushed events were never wired to the slot bar. OnSessionProgress
        // must now fire once per flushed parent batch, carrying the batch size and
        // the session's full sendable-line total.
        StubAllHookEndpoints();

        // 250 non-blank lines → batches of 100 → flushes of 100, 100, 50.
        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeNew("prog-s1", 250) },
        };

        var progressLines  = new ConcurrentBag<int>();
        var progressTotals = new ConcurrentBag<int>();

        var events = new ImportCommand.ChainWorkerEvents {
            // Opt into the sendable-line pre-count so OnSessionProgress carries a real total.
            TrackPerSessionProgress = true,
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, lines, total) => { progressLines.Add(lines); progressTotals.Add(total); },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        // Three batches were flushed and the line counts sum to the whole transcript.
        await Assert.That(progressLines.Count).IsEqualTo(3);
        await Assert.That(progressLines.Sum()).IsEqualTo(250);
        // Every report carries the same denominator: the session's sendable total.
        await Assert.That(progressTotals.All(t => t == 250)).IsTrue();
    }

    [Test]
    public async Task ImportChainsAsync_skips_line_precount_when_progress_disabled() {
        // Perf gate: when no live bar consumes the denominator
        // (non-TTY, TrackPerSessionProgress defaults false), the per-session
        // pre-count is skipped — OnSessionProgress still fires per batch but with
        // total == 0, so we never read the whole transcript just to throw it away.
        StubAllHookEndpoints();

        var chains = new List<List<ImportCommand.SessionClassification>> {
            new() { MakeNew("noprecount-s1", 250) },
        };

        var progressTotals = new ConcurrentBag<int>();

        var events = new ImportCommand.ChainWorkerEvents {
            // TrackPerSessionProgress omitted → defaults false (the non-TTY default).
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, total) => progressTotals.Add(total),
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        // Batches still flowed, but the denominator was never computed.
        await Assert.That(progressTotals.Count).IsEqualTo(3);
        await Assert.That(progressTotals.All(t => t == 0)).IsTrue();
    }

    [Test]
    public async Task ImportChainsAsync_processes_within_chain_in_order() {
        StubAllHookEndpoints();

        // Single chain of 3 sessions. They must complete in order s1 → s2 → s3.
        var chain = new List<ImportCommand.SessionClassification> {
            MakeNew("ord-s1", 50), MakeNew("ord-s2", 50), MakeNew("ord-s3", 50),
        };
        var chains = new List<List<ImportCommand.SessionClassification>> { chain };

        var order = new ConcurrentQueue<string>();
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, c, _, _) => order.Enqueue($"Loading {c.SessionId}..."),
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        var lines = order.ToArray();
        await Assert.That(lines[0]).Contains("ord-s1");
        await Assert.That(lines[1]).Contains("ord-s2");
        await Assert.That(lines[2]).Contains("ord-s3");
    }

    [Test]
    public async Task ImportChainsAsync_routes_codex_session_to_vendor_specific_url() {
        // kcap import --codex must POST to the vendor-specific route: /hooks/session-start
        // (no vendor in path) routes to the {vendor=claude} default and writes claude-tagged
        // SessionStarted events for every codex rollout.
        StubAllHookEndpoints();

        var session = MakeNew("codex-routing", 10) with { Vendor = HarnessId.Codex };
        var chains  = new List<List<ImportCommand.SessionClassification>> { new() { session } };

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None);

        var startHits = _server.LogEntries
            .Count(e => e.RequestMessage.Path == "/hooks/session-start/codex");
        var endHits = _server.LogEntries
            .Count(e => e.RequestMessage.Path == "/hooks/session-end/codex");
        var legacyHits = _server.LogEntries
            .Count(e => e.RequestMessage.Path is "/hooks/session-start" or "/hooks/session-end");

        await Assert.That(startHits).IsEqualTo(1);
        await Assert.That(endHits).IsEqualTo(1);
        await Assert.That(legacyHits).IsEqualTo(0);
    }

    [Test]
    public async Task ImportChainsAsync_dispatches_independent_chains_in_parallel() {
        // Each transcript POST parks until all four are in flight. A single worker cannot reach the
        // fourth: it does not send the next session's post until this one returns, so the gate opens
        // only when the four workers really overlap.
        using var gate = new TranscriptOverlapGate(expected: 4);
        using var client = new HttpClient(gate, disposeHandler: false);

        var chains = Enumerable.Range(0, 4)
            .Select(i => new List<ImportCommand.SessionClassification> { MakeNewNoGit($"par{i}", 50) })
            .ToList();

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        var result = await Import().ImportChainsAsync(client, "http://localhost", chains, events, CancellationToken.None);

        await Assert.That(gate.Overlapped).IsTrue();
        await Assert.That(result.Loaded).IsEqualTo(4);
    }

    /// Holds each <c>/hooks/transcript</c> POST until <paramref name="expected"/> of them are inside
    /// <see cref="SendAsync"/> together. Any other route answers immediately so the workers can reach
    /// the transcript post.
    sealed class TranscriptOverlapGate(int expected) : HttpMessageHandler {
        readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        int _inFlight;

        public bool Overlapped { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (request.RequestUri?.AbsolutePath == "/hooks/transcript") {
                if (Interlocked.Increment(ref _inFlight) == expected) {
                    Overlapped = true;
                    _release.TrySetResult();
                }

                var opened = await Task.WhenAny(_release.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
                Interlocked.Decrement(ref _inFlight);
                if (opened != _release.Task)
                    return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK) {
                Content = new StringContent("{}")
            };
        }
    }

    [Test]
    public async Task ImportChainsAsync_derives_workspace_root_from_the_remapped_cwd_not_the_raw_meta_cwd() {
        // workspace_root discovery must run GitRepository.FindRoot against the
        // remap-/worktree-resolved path the import flow computes up front into
        // sessionCwds, not the RAW transcript cwd (session.Meta.Cwd). A historical transcript whose recorded
        // cwd no longer exists (moved/renamed repo) would silently omit
        // workspace_root even though the user had configured a cwd_remap that
        // resolves to a real git working tree.
        StubAllHookEndpoints();

        var resolvedRepoDir = Path.Combine(_tempDir, "resolved-repo");
        Directory.CreateDirectory(Path.Combine(resolvedRepoDir, ".git"));

        const string sid = "workspace-root-remap";
        var path = Path.Combine(_tempDir, $"{sid}.jsonl");
        File.WriteAllLines(path, new[] {
            """{"type":"user","timestamp":"2026-03-15T10:00:00Z","cwd":"/tmp/proj","message":{"content":"line-0"}}""",
        });

        var session = new ImportCommand.SessionClassification {
            SessionId  = sid,
            FilePath   = path,
            EncodedCwd = "-tmp-proj",
            // The raw recorded cwd no longer exists on disk / has no git repo — if this
            // were used directly, FindRoot would return null and workspace_root would
            // be omitted entirely.
            Meta       = new() { Cwd = "/definitely/does-not-exist/raw-cwd" },
            Status     = ImportCommand.ClassificationStatus.New,
            TotalLines = 1,
        };
        var chains = new List<List<ImportCommand.SessionClassification>> { new() { session } };

        // Mirrors what ResolveTranscriptReposAsync populates: the remapped/worktree-resolved
        // cwd for this session, fed back to ImportSingleSessionAsync's workspace_root lookup.
        var sessionCwds = new Dictionary<string, string> { [sid] = resolvedRepoDir };

        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var result = await Import().ImportChainsAsync(client, _server.Url!, chains, events, CancellationToken.None, sessionCwds);

        await Assert.That(result.Loaded).IsEqualTo(1);

        var startEntry = _server.LogEntries.Single(e => e.RequestMessage.Path == "/hooks/session-start/claude");
        var body       = JsonNode.Parse(startEntry.RequestMessage.Body!)!.AsObject();

        await Assert.That(body["workspace_root"]?.GetValue<string>()).IsEqualTo(resolvedRepoDir);
        await Assert.That(body["cwd"]?.GetValue<string>()).IsEqualTo(resolvedRepoDir);
    }

    const string CodexSid = "0199a2b3c4d5e6f708192a3b4c5d6e7f";

    ImportCommand.SessionClassification CodexSession() => MakeNewNoGit(CodexSid, 3) with { Vendor = HarnessId.Codex };

    void WriteCodexIndex(string name, string updatedAt) {
        var codexHome = TestHarnesses.Under(Home).Of<CodexHarness>().Paths.Home;
        Directory.CreateDirectory(codexHome);
        File.WriteAllText(Path.Combine(codexHome, "session_index.jsonl"),
            $$"""{"id":"{{Guid.ParseExact(CodexSid, "N"):D}}","thread_name":"{{name}}","updated_at":"{{updatedAt}}"}""" + "\n");
    }

    async Task<int> ImportCodexCountingTitleTasks() {
        var titleTasks = 0;
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => Interlocked.Increment(ref titleTasks),
            OnBackgroundWorkReady = _ => { },
        };

        using var client = new HttpClient();
        var result = await Import().ImportChainsAsync(client, _server.Url!, [[CodexSession()]], events, CancellationToken.None);
        await Assert.That(result.Loaded).IsEqualTo(1);

        return titleTasks;
    }

    /// <summary>Codex's own index title goes to /hooks/harness-title as a rename stamped with the index's
    /// updated_at, and once the server takes it there no generated title is queued.</summary>
    [Test]
    public async Task ImportChainsAsync_posts_codex_index_title_and_skips_title_generation() {
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        WriteCodexIndex("Fix the flaky test", "2026-05-01T10:20:30Z");

        var titleTasks = await ImportCodexCountingTitleTasks();

        var posts = _server.FindLogEntries(Request.Create().WithPath("/hooks/harness-title").UsingPost());
        await Assert.That(posts.Count).IsEqualTo(1);

        var body = JsonNode.Parse(posts[0].RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(CodexSid);
        await Assert.That(body["title"]!.GetValue<string>()).IsEqualTo("Fix the flaky test");
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("rename");
        await Assert.That(body["changed_at"]!.GetValue<DateTimeOffset>()).IsEqualTo(new DateTimeOffset(2026, 5, 1, 10, 20, 30, TimeSpan.Zero));
        await Assert.That(titleTasks).IsEqualTo(0);
    }

    /// <summary>An older server takes the title only through /hooks/set-title, which fills an untitled session and
    /// nothing more, so a generated title is still queued.</summary>
    [Test]
    public async Task ImportChainsAsync_still_generates_a_codex_title_when_only_the_legacy_route_took_it() {
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/hooks/set-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        WriteCodexIndex("Fix the flaky test", "2026-05-01T10:20:30Z");

        var titleTasks = await ImportCodexCountingTitleTasks();

        await Assert.That(_server.FindLogEntries(Request.Create().WithPath("/hooks/set-title").UsingPost()).Count).IsEqualTo(1);
        await Assert.That(titleTasks).IsEqualTo(1);
    }

    [Test]
    public async Task ImportChainsAsync_posts_no_codex_title_when_the_index_names_none() {
        StubAllHookEndpoints();
        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var titleTasks = await ImportCodexCountingTitleTasks();

        await Assert.That(_server.FindLogEntries(Request.Create().WithPath("/hooks/harness-title").UsingPost()).Count).IsEqualTo(0);
        await Assert.That(titleTasks).IsEqualTo(1);
    }

    /// <summary>A Codex session the server already holds gets only its index name: no transcript, no lifecycle.</summary>
    [Test]
    public async Task PostAlreadyLoadedCodexTitles_posts_only_the_index_title() {
        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        WriteCodexIndex("Fix the flaky test", "2026-05-01T10:20:30Z");
        const string unnamed = "0199a2b3c4d5e6f708192a3b4c5d6e70";

        using var client = new HttpClient();
        await Import().PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [
            CodexSession() with { Status = ImportCommand.ClassificationStatus.AlreadyLoaded },
            MakeNewNoGit(unnamed, 3) with { Vendor = HarnessId.Codex, Status = ImportCommand.ClassificationStatus.AlreadyLoaded },
        ], new Progress<ImportProgress>(), CancellationToken.None);

        var posts = _server.LogEntries.Where(e => e.RequestMessage.Method == "POST").ToList();
        await Assert.That(posts.Select(e => e.RequestMessage.Path)).IsEquivalentTo(["/hooks/harness-title"]);

        var body = JsonNode.Parse(posts[0].RequestMessage.Body!)!;
        await Assert.That(body["session_id"]!.GetValue<string>()).IsEqualTo(CodexSid);
        await Assert.That(body["title"]!.GetValue<string>()).IsEqualTo("Fix the flaky test");
        await Assert.That(body["kind"]!.GetValue<string>()).IsEqualTo("rename");
        await Assert.That(body["changed_at"]!.GetValue<DateTimeOffset>()).IsEqualTo(new DateTimeOffset(2026, 5, 1, 10, 20, 30, TimeSpan.Zero));
    }

    [Test]
    public async Task PostAlreadyLoadedCodexTitles_posts_nothing_for_a_session_the_index_does_not_name() {
        WriteCodexIndex("Someone else", "2026-05-01T10:20:30Z");

        using var client = new HttpClient();
        await Import().PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [
            MakeNewNoGit("0199a2b3c4d5e6f708192a3b4c5d6e70", 3) with { Vendor = HarnessId.Codex, Status = ImportCommand.ClassificationStatus.AlreadyLoaded },
        ], new Progress<ImportProgress>(), CancellationToken.None);

        await Assert.That(_server.LogEntries.Count).IsEqualTo(0);
    }

    /// <summary>The index is read once per import: a later change to the file is not seen by the same run.</summary>
    [Test]
    public async Task Codex_index_is_read_once_per_import() {
        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));
        WriteCodexIndex("Before", "2026-05-01T10:20:30Z");

        var import = Import();
        var loaded = CodexSession() with { Status = ImportCommand.ClassificationStatus.AlreadyLoaded };
        using var client = new HttpClient();

        await import.PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [loaded], new Progress<ImportProgress>(), CancellationToken.None);
        WriteCodexIndex("After", "2026-05-02T10:20:30Z");
        await import.PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [loaded], new Progress<ImportProgress>(), CancellationToken.None);

        var titles = _server.LogEntries.Select(e => JsonNode.Parse(e.RequestMessage.Body!)!["title"]!.GetValue<string>()).ToList();
        await Assert.That(titles).IsEquivalentTo(["Before", "Before"]);
    }

    sealed class CancelOnHarnessTitle(CancellationTokenSource cts, bool failSessionEnd = false) : DelegatingHandler(new HttpClientHandler()) {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) {
            if (failSessionEnd && request.RequestUri!.AbsolutePath.StartsWith("/hooks/session-end", StringComparison.Ordinal))
                throw new InvalidOperationException("session-end failed");

            if (request.RequestUri!.AbsolutePath != "/hooks/harness-title") return base.SendAsync(request, ct);

            cts.Cancel();
            ct.ThrowIfCancellationRequested();

            throw new OperationCanceledException(cts.Token);
        }
    }

    /// <summary>A cancellation during the title post still closes the session, then surfaces instead of reporting the
    /// session loaded and queueing a generated title.</summary>
    [Test]
    public async Task ImportChainsAsync_codex_title_cancellation_sends_session_end_then_propagates() {
        StubAllHookEndpoints();
        WriteCodexIndex("Fix the flaky test", "2026-05-01T10:20:30Z");

        var titleTasks = 0;
        var ended      = 0;
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => { },
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => Interlocked.Increment(ref ended),
            OnTitleTaskReady      = _ => Interlocked.Increment(ref titleTasks),
            OnBackgroundWorkReady = _ => { },
        };

        using var cts    = new CancellationTokenSource();
        using var client = new HttpClient(new CancelOnHarnessTitle(cts));

        await Assert.That(async () => await Import().ImportChainsAsync(client, _server.Url!, [[CodexSession()]], events, cts.Token))
                    .Throws<OperationCanceledException>();

        await Assert.That(_server.LogEntries.Count(e => e.RequestMessage.Path == "/hooks/session-end/codex")).IsEqualTo(1);
        await Assert.That(titleTasks).IsEqualTo(0);
        await Assert.That(ended).IsEqualTo(0);
    }

    /// <summary>A held cancellation surfaces even when the session-end sent after it fails, on both the new and the
    /// resumed path, rather than the failure turning the session into an ordinary error.</summary>
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ImportChainsAsync_codex_title_cancellation_propagates_when_session_end_fails(bool resumed) {
        StubAllHookEndpoints();
        WriteCodexIndex("Fix the flaky test", "2026-05-01T10:20:30Z");

        var errored = 0;
        var events = new ImportCommand.ChainWorkerEvents {
            OnSessionStarted      = (_, _) => { },
            OnSubagentStarted     = (_, _, _) => { },
            OnSubagentFinished    = (_, _, _, _) => { },
            OnSessionProgress     = (_, _, _) => { },
            OnSessionErrored      = (_, _, _) => Interlocked.Increment(ref errored),
            OnSessionWarning      = (_, _, _) => { },
            OnSessionEnded        = (_, _, _, _) => { },
            OnTitleTaskReady      = _ => { },
            OnBackgroundWorkReady = _ => { },
        };

        using var cts    = new CancellationTokenSource();
        using var client = new HttpClient(new CancelOnHarnessTitle(cts, failSessionEnd: true));
        var session = resumed
            ? CodexSession() with { Status = ImportCommand.ClassificationStatus.Partial, ResumeFromLine = 1 }
            : CodexSession();

        await Assert.That(async () => await Import().ImportChainsAsync(client, _server.Url!, [[session]], events, cts.Token))
                    .Throws<OperationCanceledException>();
        await Assert.That(errored).IsEqualTo(0);
    }

    sealed class CollectingProgress : IProgress<ImportProgress> {
        public ConcurrentQueue<ImportProgress> Reports { get; } = new();

        public void Report(ImportProgress value) => Reports.Enqueue(value);
    }

    [Test]
    public async Task PostAlreadyLoadedCodexTitles_reports_a_title_the_server_refused() {
        _server.Given(Request.Create().WithPath("/hooks/harness-title").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(422));
        WriteCodexIndex("Fix the flaky test", "2026-05-01T10:20:30Z");

        var progress = new CollectingProgress();
        using var client = new HttpClient();
        await Import().PostAlreadyLoadedCodexTitlesAsync(client, _server.Url!, [
            CodexSession() with { Status = ImportCommand.ClassificationStatus.AlreadyLoaded },
        ], progress, CancellationToken.None);

        var warning = progress.Reports.OfType<ImportTitleNotRecorded>().SingleOrDefault();
        await Assert.That(warning).IsNotNull();
        await Assert.That(warning!.SessionId).IsEqualTo(CodexSid);
    }
}
