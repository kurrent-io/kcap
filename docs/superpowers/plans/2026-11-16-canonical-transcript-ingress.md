# Canonical Transcript Ingress Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The kcap client projects Claude transcript lines to canonical events and sends them to a vendor-neutral server ingress; the server stores them without running a normalizer.

**Architecture:** The client gains a wire batch (`CanonicalTranscriptBatch`), an assembler that turns redacted lines into that batch with the transcript library, and a per-source `CanonicalRoute` every producer of raw batches calls before it posts. The server gains a route and a hub method that convert the batch into the pipeline's existing `NormalizedBatchEntry` list and run the rest of `ProcessTranscriptBatchAsync` unchanged. One server capability, one configuration switch, fallback to raw lines on refusal.

**Tech Stack:** .NET 10, NativeAOT (System.Text.Json source generation only), SignalR, TUnit, WireMock.Net; on the server ASP.NET minimal APIs, KurrentDB, Postgres.

**Spec:** `docs/superpowers/specs/2026-11-16-canonical-transcript-ingress-design.md`. Read it before any task.

## Global Constraints

- Two repositories. Phase A and C are `kcap` (this one). Phase B is `kcap-server`, done in a fresh clone or worktree of GitHub `main`, never in the owner's checkout. Each phase is its own pull request; the owner merges.
- Event ids, canonical event shapes and the accepted parity exemptions do not change.
- Criterion 1: the new path stores the same or more than the raw path, never less, and the same usage and cost numbers.
- The wire `format` is `1`. JSON property names are snake_case, like `TranscriptBatch`.
- Canonical vendor list in the first release: `claude` only.
- The server switch is off by default. With it off, or with an older server, every client stays on raw lines.
- Anything the server does not understand (format, event type, payload field, metadata key, unparseable payload) refuses the whole batch as unsupported and stores nothing.
- The server sets `$ingester_user_id`, owner, visibility, agent host, origin and delivery state itself.
- No new Postgres table.
- AOT: no reflection-based serialization; after Phase A and Phase C run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` and expect no output.
- Repository rules in `CLAUDE.md` apply: comments, commit subjects `one clause (#2638)`, one type per file, `TempDir` in tests, TUnit `--treenode-filter`.
- One refinement of the spec's batch table, decided here: `lines`, `questions` and `lastLineTimestamp` are one list of per-line records (`number`, `uuid`, `parent_uuid`, `record_type`, `timestamp`). The server's pipeline reads exactly those four fields from a raw line after normalisation (`TranscriptEntry`), so the list replaces all three.

## Review Focus

1. A transcript missing or shorter than the resume line: the replay is incomplete, so the producer sends raw lines; it does not throw or stall. A rewrite that keeps the file at least that long is not detected, as on the raw path, which resumes by line number alone.
2. One line larger than the chunk limit (a pasted image): it still goes out, as a batch of one line.
3. The server refuses a chunk as unsupported in the middle of a split read: that chunk and the rest go raw, with the same observed commits and `more_follows`.
4. A subagent transcript (`agentId` set, sidechain records): no title sightings and no turn end are sent.
5. A truncated or non-JSON line between good lines: it is covered by `lines`, produces nothing, and the frontier moves past it.

Each has a test in the task that owns the code (A4, A4, C2, A3, A4).

## File Structure

`kcap`:

| File | Responsibility |
|---|---|
| `src/Capacitor.Models.Transcripts/CanonicalEventJson.cs` | gains `Write`, the inverse of `TryParse` |
| `src/Capacitor.Models.Transcripts/LineSignals.cs`, `ILineSignalReader.cs`, `TitleSighting.cs`, `TranscriptSignals.cs` | what a line says besides events, per vendor |
| `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeLineSignals.cs` | Claude's reader |
| `src/Capacitor.Cli.Core/Transcripts/Canonical*.cs` | wire records, one per file |
| `src/Capacitor.Cli.Core/Transcripts/CanonicalBatchAssembler.cs` | lines → wire batch, projection state |
| `src/Capacitor.Cli.Core/Transcripts/CanonicalVendors.cs`, `CanonicalRoute.cs`, `IngestCapability.cs` | which vendors, per-source routing, capability probe |
| Producers: `TranscriptRecorder.cs`, `IRecorderTransport.cs`, `HubRecorderTransport.cs`, `ServerRecorderTransport.cs`, `WatcherManager.cs`, `SessionImporter.cs`, `LifecycleSpoolDrain.cs`, `TranscriptSpool.cs` | call the route |

`kcap-server`:

| File | Responsibility |
|---|---|
| `src/Capacitor.Api.Public.Abstractions/Sessions/CanonicalTranscriptBatch.cs` (and parts) | server copy of the wire records |
| `src/Capacitor.Server.Services/Sessions/Canonical/Ingress/CanonicalBatchReader.cs` | batch → entries, or a refusal; all trust rules |
| `src/Capacitor.Server.Services/Sessions/SessionWriter.TranscriptPipeline.cs` | takes entries from either source |
| `src/Capacitor.Api.Public/Hooks/HookEndpoints.cs`, `SessionActivityHookHandlers.cs`, `Sessions/CapacitorHub.cs` | route, hub method, capability |
| `test/Capacitor.Server.Tests.Ingest/Parity/` | wire parity mode |

---

## Phase A — `kcap`: library and wire, no caller

### Task A1: Settle the usage difference before anything depends on it

**Files:**
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeServerDeltaTests.cs`
- Modify: `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudeTranscriptEvents.cs:60-70`

A review reported that a noise record (`progress`, `system`) between two assistant records with identical usage clears `PreviousUsage` in the library but not in the server's `ClaudeCodeNormalizer` (`Normalize`, lines 49–76 at server `225d2208f`), so the library would stamp the second usage as new.

- [ ] **Step 1:** Read both code paths and decide which is true. Record the answer in the PR description.
- [ ] **Step 2:** Write the test.

```csharp
[Test]
public async Task A_noise_record_between_two_assistant_records_keeps_the_usage_an_echo() {
    var ctx = Ctx();
    E(Assistant(uuid: Record, usage: "{\"input_tokens\":10,\"output_tokens\":5}"), ctx);
    E("""{"type":"progress","uuid":"a1b2c3d4-0000-4000-8000-0000000000p1"}""", ctx);
    var second = E(Assistant(uuid: Resubmit, usage: "{\"input_tokens\":10,\"output_tokens\":5}"), ctx);

    await Assert.That(second[0].UsageIsEcho).IsTrue();
}
```

`Assistant(uuid, usage)` is a local helper building one assistant line with a text block.

- [ ] **Step 3:** Run `dotnet run --project test/Capacitor.Models.Transcripts.Tests.Unit -- --treenode-filter "/*/*/ClaudeServerDeltaTests/*"`. If it passes, the report was wrong: keep the test, change nothing. If it fails, make the library match the server and rerun.
- [ ] **Step 4:** Commit: `Keep a usage an echo across a Claude noise record (#2638)`.

### Task A2: Write canonical payloads as stored JSON

**Files:**
- Modify: `src/Capacitor.Models.Transcripts/CanonicalEventJson.cs`, `TranscriptEventJsonContext.cs`
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/CanonicalEventJsonTests.cs`

**Interfaces:**
- Produces: `public static string CanonicalEventJson.Write(object payload)`; throws `ArgumentException` for a type `CanonicalEventTypes.Of` does not know.

- [ ] **Step 1:** Write the test: one case per constant in `CanonicalEventTypes` (use the sample payload builders the existing `TryParse` tests have, add the missing ones), asserting

```csharp
var json = CanonicalEventJson.Write(payload);
var back = CanonicalEventJson.TryParse(CanonicalEventTypes.Of(payload), json);
await Assert.That(CanonicalEventJson.Write(back!)).IsEqualTo(json);
```

plus `Write(new object())` throws.

- [ ] **Step 2:** Run it; expect a compile failure on `Write`.
- [ ] **Step 3:** Implement. Protobuf messages go through `JsonFormatter` with the settings that are the inverse of the parser `TryParse` uses; the System.Text.Json records go through `TranscriptEventJsonContext`, which gains every non-protobuf payload type `CanonicalEventTypes.Of` lists.
- [ ] **Step 4:** Run; expect all cases to pass.
- [ ] **Step 5:** Commit: `Write canonical event payloads as stored JSON (#2638)`.

### Task A3: Line signals for Claude

**Files:**
- Create: `src/Capacitor.Models.Transcripts/LineSignals.cs`, `TitleSighting.cs`, `ILineSignalReader.cs`, `TranscriptSignals.cs`, `Harness/Claude/ClaudeLineSignals.cs`
- Test: `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Claude/ClaudeLineSignalsTests.cs`

**Interfaces:**
- Produces:

```csharp
public sealed record TitleSighting(string Kind, string Title, DateTimeOffset? OwnTime, bool Legacy); // Kind: "auto" | "rename"
public sealed record LineSignals(string? Uuid, string? ParentUuid, string? RecordType, string? Timestamp, TitleSighting? Title, bool EndsTurn) {
    public static readonly LineSignals None = new(null, null, null, null, null, false);
}
public interface ILineSignalReader { LineSignals Read(string line, string sessionId, string? agentId); }
public static class TranscriptSignals { public static ILineSignalReader? For(string vendor); } // "claude" only
```

The behaviour to match is the server's, at `225d2208f`: `ClaudeHarnessTitleExtractor.TryExtract` and `LineTimestamp` (titles and timestamp), `ClaudeCodeNormalizer.EndsTurn` (turn end), and `TranscriptEntry` (`uuid`, `parentUuid`, `type`, `timestamp` read verbatim). Port that extractor's test cases.

- [ ] **Step 1:** Write tests, each a line in and a `LineSignals` out:
  - `custom-title` for this session → `Title` with `Kind == "rename"`, `OwnTime == null`
  - `ai-title` → `Kind == "auto"`
  - a legacy `summary` line → `Legacy == true`
  - a title line naming another session id → `Title == null`
  - `{"type":"system","subtype":"turn_duration"}` → `EndsTurn`; the same with `"isSidechain":true` → not
  - an assistant record → `Uuid`, `ParentUuid`, `RecordType == "assistant"`, `Timestamp` copied as written
  - `agentId: "a1"` with a `custom-title` line and a `turn_duration` line → `Title == null`, `EndsTurn == false`
  - `"{not json"` → `LineSignals.None`, no exception
- [ ] **Step 2:** Run; expect compile failures.
- [ ] **Step 3:** Implement. Reuse `ClaudeTitleLine` for the cheap pre-check.
- [ ] **Step 4:** Run; expect pass.
- [ ] **Step 5:** Commit: `Read titles, turn ends and record ids from a Claude line (#2638)`.

### Task A4: Wire records and the assembler

**Files:**
- Create under `src/Capacitor.Cli.Core/Transcripts/`: `CanonicalTranscriptBatch.cs`, `CanonicalBatchLine.cs`, `CanonicalBatchEvent.cs`, `CanonicalBatchTitle.cs`, `CanonicalBatchAttachment.cs`, `CanonicalBatchCaptureLoss.cs`, `CanonicalBatchAssembler.cs`
- Modify: `src/Capacitor.Cli.Core/Models.cs` (register `CanonicalTranscriptBatch` on `CapacitorJsonContext`, near line 1341), `src/Capacitor.Models.Transcripts/CanonicalEvent.cs` (`TranscriptAttachment` gains `Guid MessageId` and `int BlockIndex`, set by the Claude projection where it derives the id)
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Transcripts/CanonicalBatchAssemblerTests.cs`

**Interfaces:**
- Produces (JSON names in comments where not the snake_case of the property):

```csharp
public sealed record CanonicalTranscriptBatch {
    public int Format { get; init; } = 1;
    public required string SessionId { get; init; }
    public string? AgentId { get; init; }
    public required string Vendor { get; init; }
    public required CanonicalBatchLine[] Lines { get; init; }
    public required CanonicalBatchEvent[] Events { get; init; }
    public CanonicalBatchTitle[]? Titles { get; init; }
    public int? TurnEndLine { get; init; }
    public CanonicalBatchCaptureLoss[]? CaptureLoss { get; init; }
    public RepositoryPayload? Repository { get; init; }
    public bool Strict { get; init; }
    public ObservedCommit[]? ObservedCommits { get; init; }
    public bool MoreFollows { get; init; }
}
public sealed record CanonicalBatchLine(int Number, string? Uuid, string? ParentUuid, string? RecordType, string? Timestamp);
public sealed record CanonicalBatchEvent {
    public required string Type { get; init; }
    public required JsonElement Payload { get; init; }
    public required Guid EventId { get; init; }
    public required int Line { get; init; }
    public required DateTimeOffset Timestamp { get; init; }
    public string? RecordTimestamp { get; init; }
    public string? CausedBy { get; init; }
    public JsonElement? Usage { get; init; }            // TokenUsage as CanonicalEventJson writes it
    public bool UsageIsEcho { get; init; }
    public long? CacheCreationTokens { get; init; }
    public Dictionary<string, JsonElement>? VendorMetadata { get; init; }
    public CanonicalBatchAttachment[]? Attachments { get; init; }
    public JsonElement? Stamp { get; init; }            // reserved, never written in this plan
}
public sealed record CanonicalBatchTitle(string Kind, string Title, DateTimeOffset? OwnTime, bool Legacy, int Line);
public sealed record CanonicalBatchAttachment(Guid Id, Guid MessageId, int BlockIndex, string FileName, string ContentType, byte[] Data);
public sealed record CanonicalBatchCaptureLoss(int Line, string Raw);

public sealed class CanonicalBatchAssembler(ITranscriptProjection projection, ILineSignalReader signals, string sessionId, string? agentId, string vendor) {
    public void Replay(string line, int lineNumber, DateTimeOffset receivedAt);            // state only
    public CanonicalTranscriptBatch Assemble(TranscriptBatch raw, DateTimeOffset receivedAt);
    public void Acknowledged();                                                             // MarkPersisted for the last Assemble
}
```

Behaviour, from the spec's section 3 and the server's `LibraryProjectionNormalizer` (the reference implementation of "apply a projection result"):
- `BeginBatch` at the start of `Assemble` and of each replayed line's batch boundary; `Acknowledged` calls `MarkPersisted` for every event id of the last assembled batch.
- An amendment is merged into its target's payload extensions when the target is in the same batch, and dropped otherwise.
- `UsageApplied` stamps same-batch targets and emits the backfill event for targets already acknowledged.
- A line starting with kcap's capture-loss marker goes to `CaptureLoss` verbatim and is not projected. Find the marker where the recorder writes it (`kcap_capture_loss`).
- A rejected line appears in `Lines` and nowhere else.
- `Lines` has one record per raw line, in order, from `signals.Read`. `Titles` and `TurnEndLine` come from the same read.
- `Repository`, `Strict`, `ObservedCommits`, `MoreFollows` are copied from `raw`.

- [ ] **Step 1:** Write tests:
  - `Two_lines_become_their_events_in_line_order`: a user line and an assistant line → `Events` types `UserMessageReceived`, `AssistantTextGenerated`, `Line` 0 and 1, `EventId`s equal to what `ClaudeTranscriptEvents.Instance.Project` gives for the same lines.
  - `A_line_without_events_is_still_covered`: a `progress` line → `Lines.Length == 1`, `Events` empty.
  - `A_truncated_line_between_good_lines_is_covered_and_produces_nothing` (Review Focus 5).
  - `A_capture_loss_line_travels_verbatim`.
  - `An_image_line_carries_its_bytes_message_id_and_block_index`.
  - `One_line_of_five_megabytes_assembles` (Review Focus 2): a user line with a 5 MiB base64 image → one event, one attachment.
  - `A_title_and_a_turn_end_are_reported_with_their_lines`.
  - `Replay_then_assemble_equals_one_continuous_assemble`: assemble lines 0..9 in one assembler; in another, `Replay` 0..4 then `Assemble` 5..9; the second's batch equals the last five lines' share of the first (compare serialized JSON of events with `Line >= 5`).
  - `Acknowledged_events_make_a_later_usage_a_backfill_not_a_stamp` (use a Codex fixture; the assembler is vendor-neutral).
  - `The_batch_round_trips_through_CapacitorJsonContext`, asserting `format`, `session_id`, `turn_end_line` and `more_follows` appear under those names.
- [ ] **Step 2:** Run `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit -- --treenode-filter "/*/*/CanonicalBatchAssemblerTests/*"`; expect compile failures.
- [ ] **Step 3:** Implement the records and the assembler.
- [ ] **Step 4:** Run; expect pass. Run the AOT publish check from Global Constraints.
- [ ] **Step 5:** Commit: `Assemble a canonical transcript batch from redacted lines (#2638)`.

Open the Phase A pull request. Phase B needs its merge commit.

---

## Phase B — `kcap-server`: the ingress

Work in a fresh clone; set `src/cli` to the Phase A merge commit first (the recipe for running the parity suite against a CLI commit is in the session memory `library-parity-suite-recipe`). Server commit subjects carry the `[AI-5514]` prefix that repository uses.

### Task B1: Wire records, capability, switch

**Files:**
- Create: `src/Capacitor.Api.Public.Abstractions/Sessions/CanonicalTranscriptBatch.cs` and one file per nested record, mirroring Task A4's JSON exactly
- Modify: `src/Capacitor.Api.Public/Hooks/HookEndpoints.cs`, `SessionActivityHookHandlers.cs`, `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs`
- Test: the hook endpoint test project that covers `/hooks/transcript`

**Interfaces:**
- Produces:
  - Configuration key `Ingest:CanonicalEnabled`, bool, default `false`.
  - `GET /api/ingest/capabilities` → `200 {"canonical_format":1}` when on, `{"canonical_format":0}` when off. Authorized like `/hooks/transcript`.
  - Hub method `Task<CanonicalCapabilities> IngestCapabilities()`, the same `canonical_format` object the HTTP route returns.
  - `POST /hooks/transcript/canonical` with the same filters as `/hooks/transcript` (`RequireAuthorization`, `RefuseDeletedSessions`, `RefuseForeignSessions`). Replies: `200 TranscriptProgress`; `422 {"error":"canonical_unsupported","reason":"…"}`; the raw route's `400`, `403`, `410`, `500` unchanged.
  - Hub method `Task<CanonicalBatchAck> SendCanonicalBatch(CanonicalTranscriptBatch batch)`, `record CanonicalBatchAck(int NextLineNumber, bool Unsupported, string? Reason)`.
  - `SessionActivityHookHandlers.HandleCanonicalTranscript(CanonicalTranscriptBatch, CancellationToken)`, which in this task refuses everything as unsupported when the switch is off and throws `NotImplementedException` when on.

- [ ] **Step 1:** Tests: capability is `0` by default and `1` with the key set; a canonical post with the switch off is `422 canonical_unsupported`; an unauthenticated post is `401`; a post to another user's session is `403 session_not_owned`.
- [ ] **Step 2:** Run; expect failures. **Step 3:** Implement. **Step 4:** Run; expect pass.
- [ ] **Step 5:** Commit: `[AI-5514] Add the canonical ingress route, capability and switch`.

### Task B2: Read a batch into entries, with every trust rule

**Files:**
- Create: `src/Capacitor.Server.Services/Sessions/Canonical/Ingress/CanonicalBatchReader.cs`, `CanonicalBatchRefusal.cs`, `VendorMetadataKeys.cs`
- Test: `test/Capacitor.Server.Tests.Ingest/Canonical/CanonicalBatchReaderTests.cs`

**Interfaces:**
- Consumes: B1's records; `NormalizedEvent`, `NormalizedBatchEntry`, `ExtractedAttachment`, `TranscriptEntry`, `HarnessTitleLine`, `CaptureLossParser`.
- Produces:

```csharp
public sealed record CanonicalBatchContent(
    IReadOnlyList<SessionWriter.NormalizedBatchEntry> Entries,
    IReadOnlyList<(HarnessTitleLine Title, int Line)> Titles,
    int? TurnEndLine,
    IReadOnlyList<(int Line, DateTimeOffset At)> LineTimestamps);
public sealed record CanonicalBatchRefusal(string Reason);
public static class CanonicalBatchReader {
    public static OneOf<CanonicalBatchContent, CanonicalBatchRefusal> Read(CanonicalTranscriptBatch batch, string sessionScope);
}
```

(Use the repository's own result type if it has one in place of `OneOf`.)

Rules:
- `Format != 1`, a `Type` the server's event type map does not know, a payload that does not parse **strictly** (unknown fields are an error), or a `VendorMetadata` key outside `VendorMetadataKeys.Allowed` → refusal. Build `Allowed` from every key the library writes to `VendorMetadata` and `VendorMetadataValues` at the pinned commit; a test enumerates the library's writes so a new key fails the build here.
- Metadata is built exactly as `LibraryProjectionNormalizer.Convert` and its `Stamp` do, from the named fields. Nothing else reaches metadata.
- Payload conversion is `LibraryProjectionNormalizer.Convert`'s switch, moved to production.
- On `SessionStarted`: remove `extensions.capacitor.owner_user_id`, `agent_host_id`, `session_origin`. Leave `default_visibility` to the existing merge and `StampUnset` logic.
- On user messages: remove a `CapacitorUndeliveredBlock`.
- Attachments: recompute `ClaudeCodeNormalizer.DeriveAttachmentId(sessionScope, MessageId, BlockIndex)` (make it reachable); a mismatch with `Id` → refusal `attachment_id`. Any `StreamName` in the payload's attachment info is cleared.
- Each event's entry carries a `TranscriptEntry` built from its line's record (`Uuid`, `ParentUuid`, `Type = RecordType`, `Timestamp`).
- `CaptureLoss` entries go through `CaptureLossParser.Parse` and become entries as `NormalizeLine` makes them.

- [ ] **Step 1:** Tests, one per rule above, each sending a hostile or unknown value and asserting the refusal or the cleaned entry; plus `A_valid_batch_gives_the_entries_the_adapter_gives` comparing with `LibraryProjectionNormalizer` on the same lines for one Claude golden case.
- [ ] **Step 2–4:** Run, implement, run.
- [ ] **Step 5:** Commit: `[AI-5514] Read a canonical batch into pipeline entries`.

### Task B3: Run entries through the pipeline

**Files:**
- Modify: `src/Capacitor.Server.Services/Sessions/SessionWriter.TranscriptPipeline.cs`, the unstored-lines store, `SessionActivityHookHandlers.cs`
- Test: `test/Capacitor.Server.Tests.Ingest/Canonical/CanonicalIngressTests.cs`

**Interfaces:**
- Produces: `SessionWriter.ProcessCanonicalBatchAsync(CanonicalBatchContent content, string vendor, string sessionId, string? agentId, int[] lineNumbers, bool strict, IReadOnlyList<ReportedCommit>? observedCommits, CancellationToken ct) : Task<TranscriptBatchResult>`.

Approach: split `ProcessTranscriptBatchAsync` at the point where raw lines have become entries, titles, a turn-end line and line timestamps. The raw overload produces those with the normalizer and the title extractor; the canonical overload takes them from `content`. Everything before (canonical ids, deletion check, gate, high-water filter, held-line load) and after (sort, seen ids, writes, attachments, commits, stamper, side effects, frontier) is shared code, not a copy.

- `$ingester_user_id` is overwritten for canonical entries (the raw path fills only when missing).
- `context.LastLineTimestamp` is advanced from `LineTimestamps`.
- Held lines: a failed or title-only-failed line is stored in `unstored_transcript_lines.line` as a JSON envelope `{"canonical":1,"line":{…},"events":[…],"titles":[…],"turn_end":bool}`. A raw transcript line never starts with `{"canonical":1`, so the retry tells them apart by that prefix. The retry of a canonical row re-enters through `CanonicalBatchReader`.

- [ ] **Step 1:** Tests against the real pipeline (Testcontainers, as the Ingest suite does):
  - `A_claude_session_through_the_ingress_resolves_no_normalizer`: wrap the normalizer selector in a counting fake; expect zero resolutions.
  - `Sending_raw_then_canonical_appends_nothing_twice` and the reverse.
  - `A_switch_at_a_turn_boundary_gives_the_single_path_stream`, both directions.
  - `A_held_title_only_line_is_retried_after_a_restart`: fail the title write once, recreate the writer, send the next batch, assert the title event exists with the right source line.
  - `A_rename_after_a_fallback_is_timed_from_the_last_canonical_line`.
  - `The_ingester_is_the_caller_whatever_the_batch_says`.
- [ ] **Step 2–4:** Run, implement, run. Then run the whole Ingest suite: the raw path must be untouched.
- [ ] **Step 5:** Commit: `[AI-5514] Store canonical batches through the transcript pipeline`.

### Task B4: Parity through the wire

**Files:**
- Modify: `test/Capacitor.Server.Tests.Ingest/Parity/LibraryParityTests.cs`, `TranscriptPipelineRun.cs`
- Create: `test/Capacitor.Server.Tests.Ingest/Parity/WireProjectionRun.cs`

- [ ] **Step 1:** Add a second data source to `LibraryParityTests`: for `Vendor.Claude`, each golden case's lines go through `CanonicalBatchAssembler` (from `src/cli`), are serialized with `CapacitorJsonContext`, deserialized as the server's `CanonicalTranscriptBatch`, and posted through `HandleCanonicalTranscript` with the switch on, using the case's `BatchPlan` for batch boundaries. Stored events, title events and turn-end confirmations are compared with the golden baseline under the same `ParityExemptions`.
- [ ] **Step 2:** Run `dotnet run -- --treenode-filter "/*/*/LibraryParityTests/*"` in `test/Capacitor.Server.Tests.Ingest`. Expected: every Claude wire case passes with no new exemption. A difference is a defect in Phase A or B to fix, not to exempt, unless it is one of the "more" cases the spec names; those get an exemption whose text says so.
- [ ] **Step 3:** Commit: `[AI-5514] Run Claude parity through the canonical wire`.

Open the Phase B pull request.

---

## Phase C — `kcap`: every producer on the route

### Task C1: Capability and route

**Files:**
- Create: `src/Capacitor.Cli.Core/Transcripts/CanonicalVendors.cs`, `IngestCapability.cs`, `CanonicalRoute.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Transcripts/CanonicalRouteTests.cs`, `IngestCapabilityTests.cs`

**Interfaces:**
- Produces:

```csharp
public static class CanonicalVendors { public static bool Contains(string? vendor); }   // null and "claude" → true; nothing else
public sealed class IngestCapability(HttpClient client, string baseUrl, TimeProvider time) {
    public Task<bool> SupportsCanonicalAsync(CancellationToken ct);   // GET /api/ingest/capabilities, cached for the process; 404 or failure → false, not cached on failure
}
public sealed class CanonicalRoute {
    public static CanonicalRoute? For(string? vendor, string sessionId, string? agentId, bool serverSupports);  // null → send raw
    public bool Active { get; }                                           // false after FallBack
    public Task<bool> ReplayAsync(string transcriptPath, int throughLineExclusive, CancellationToken ct);  // raw lines, shared read; false when a line before the bound was not replayed
    public CanonicalTranscriptBatch Encode(TranscriptBatch raw, DateTimeOffset receivedAt);
    public void Acknowledged();
    public void FallBack();
    public const string HttpPath = "/hooks/transcript/canonical";
    public const string HubMethod = "SendCanonicalBatch";
}
```

`ReplayAsync` reads with the `SharedFileText` helpers (the agent owns the file).

- [ ] **Step 1:** Tests: `For` is null for `codex`, for `claude` with `serverSupports: false`; non-null for `claude` and for a null vendor with support. `ReplayAsync` on a missing file and on a file shorter than the bound returns false without throwing, and the caller sends raw (Review Focus 1). Capability: WireMock `200 {"canonical_format":1}` → true and one request for two calls; `404` → false; `{"canonical_format":0}` → false; a `500` → false and the next call asks again.
- [ ] **Step 2–4:** Run, implement, run.
- [ ] **Step 5:** Commit: `Route a transcript source to the canonical ingress (#2638)`.

### Task C2: HTTP producers

**Files:**
- Modify: `src/Capacitor.Cli/WatcherManager.cs` (inline drain, around line 863), `src/Capacitor.Cli/Commands/SessionImporter.cs` (around line 660), `src/Capacitor.Cli.Core/LifecycleSpoolDrain.cs`, `src/Capacitor.Cli.Core/TranscriptSpool.cs` (`BuildTranscriptSpoolBatch` gains an optional `transcript_path`)
- Create: `src/Capacitor.Cli.Core/Transcripts/TranscriptPost.cs`
- Test: the existing test classes of those three producers, plus `test/Capacitor.Cli.Core.Tests.Unit/Transcripts/TranscriptPostTests.cs`

**Interfaces:**
- Consumes: C1.
- Produces:

```csharp
public static class TranscriptPost {
    // Posts one already-split batch. Canonical when route is active; on 422 canonical_unsupported calls route.FallBack() and posts the same batch raw, once.
    public static Task<HttpResponseMessage> SendAsync(HttpClient client, string baseUrl, TranscriptBatch batch, CanonicalRoute? route, TimeProvider time, Func<bool>? mayAttempt, CancellationToken ct);
}
```

It keeps each producer's `PostWithRetryAsync` behaviour and returns the final response, so the producers' own status handling (spooling through `HookSpool.IsRetryable`, the Cursor `409`, strict import aborts) is untouched. On a `2xx` to the canonical path it calls `route.Acknowledged()`.

Per producer:
- **Inline drain:** build the route from the drain's vendor and `IngestCapability`; `ReplayAsync(transcriptPath, firstLineToSend)` before the first post.
- **Import:** one route per stream (session, and each subagent); no replay, import starts at line zero. The per-vendor `*ImportSource`s that post on their own are not Claude and stay as they are.
- **Spool drain:** the spooled JSON stays a raw `TranscriptBatch`. On drain, deserialize it; when its vendor is canonical and the server supports it, build a route, replay from `transcript_path` when present, and post through `TranscriptPost`. A tail that starts above line zero goes canonical only when the replay covered every earlier line; a spool entry without `transcript_path` (written by an older CLI) drains raw.

- [ ] **Step 1:** Tests:
  - `TranscriptPost`: canonical `200` → one request to the canonical path, `Acknowledged` effect visible on the next encode; `422 canonical_unsupported` → a second request to `/hooks/transcript` with the identical raw body, route inactive afterwards; a `500` → returned as is, no raw retry; a null route → raw only.
  - `A_refused_chunk_and_the_rest_of_a_split_read_go_raw_with_their_commits` (Review Focus 3): three chunks, the second refused; requests are canonical, canonical+raw, raw; `observed_commits` and `more_follows` equal on the raw resends.
  - Inline drain: with capability on, a Claude drain from line 40 posts canonical and the events' ids equal a from-zero projection of the same file.
  - Import: a Claude import posts canonical only; a Codex import posts raw only.
  - Spool drain: an entry starting above line zero without `transcript_path` drains raw; with the capability off every entry drains raw, byte-identical to the spooled JSON.
- [ ] **Step 2–4:** Run, implement, run.
- [ ] **Step 5:** Measure the replay: time an inline drain's `ReplayAsync` over a 200 MB Claude transcript (generate one by repeating a fixture). Put the number in the PR description. If it exceeds two seconds, stop and report before continuing; the hook runs inside Claude's SessionEnd.
- [ ] **Step 6:** Commit: `Send Claude drains, imports and spooled tails as canonical batches (#2638)`.

### Task C3: The live recorder

**Files:**
- Modify: `src/Capacitor.Cli.Recording/IRecorderTransport.cs`, `TranscriptRecorder.cs` (`DrainAndSendAsync`, the replay on resume), `TranscriptReplay.cs`, `src/Capacitor.Cli/Commands/HubRecorderTransport.cs`, `src/Capacitor.Cli.Daemon/Services/ServerRecorderTransport.cs`
- Test: `test/Capacitor.Cli.Recording.Tests.Unit/TranscriptRecorderTests.cs`, with the existing fake transport extended

**Interfaces:**
- Consumes: C1.
- Produces on `IRecorderTransport`:

```csharp
Task<bool> SupportsCanonicalAsync(CancellationToken ct);                                  // hub "IngestCapabilities"; unknown method → false
Task<CanonicalSendResult> SendCanonicalBatchAsync(CanonicalTranscriptBatch batch, CancellationToken ct);
public readonly record struct CanonicalSendResult(int NextLineNumber, bool Unsupported);
```

Recorder behaviour:
- After each `ConnectAsync` and each `Reconnected`, ask `SupportsCanonicalAsync` and build a fresh `CanonicalRoute` for the source (a reconnect re-enables a route that fell back).
- The route is one more consumer of `TranscriptReplay` on resume, fed raw lines through `Replay`.
- In `DrainAndSendAsync`, after `StampLines` and the split, each chunk goes through `route.Encode` and `SendCanonicalBatchAsync` when the route is active; `Unsupported` → `FallBack` and `SendBatchAsync` for the same chunk. A sent canonical chunk calls `Acknowledged`.
- `FinishAsync` spools raw batches as today, with `transcript_path`.

- [ ] **Step 1:** Tests:
  - `A_claude_source_sends_canonical_batches_when_the_server_supports_them` and `…sends_raw_lines_when_it_does_not`.
  - `A_codex_source_sends_raw_lines_whatever_the_server_supports`.
  - `An_unsupported_reply_resends_the_chunk_raw_and_stays_raw_until_reconnect`.
  - `A_recorder_restarted_mid_session_sends_the_events_an_uninterrupted_one_sends`: run one recorder over a 60-line fixture; run another that is stopped after 30 lines and replaced by a fresh one resuming at 30; the concatenated canonical events are equal (ids, usage echo flags, retractions).
  - `A_send_failure_leaves_the_cursor_and_the_next_poll_resends_the_same_events`.
- [ ] **Step 2–4:** Run, implement, run. Run the full solution tests: `dotnet test --solution Capacitor.slnx`.
- [ ] **Step 5:** AOT publish check from Global Constraints.
- [ ] **Step 6:** Commit: `Record Claude sessions through the canonical ingress (#2638)`.

Open the Phase C pull request. Its description states that nothing changes until a server turns `Ingest:CanonicalEnabled` on.

### Task C4: Turn it on in beta

Owner-gated, not for an agent to do alone.

- [ ] Server with Phase B deployed to beta, switch on. Record one Claude session with a Phase C CLI, one with the released CLI; compare both in the web app, the desktop app and mobile (criteria 1 and 4).
- [ ] `kcap import` the first session again; the stream length does not change (criterion 2).
- [ ] Turn the switch off while a session records; the session continues with no gap (brake).

---

## Self-review notes

- Spec sections 1–5 map to A2–A4, B1–B3, C1–C3; verification to B3, B4, C2, C3, C4; the stamp is the reserved `Stamp` field only; later vendors are not planned here.
- The spec's "capability once per connection or process" is C1 (`IngestCapability`) and C3 (`SupportsCanonicalAsync`).
- Deviation recorded in Global Constraints: per-line records replace three batch fields.
