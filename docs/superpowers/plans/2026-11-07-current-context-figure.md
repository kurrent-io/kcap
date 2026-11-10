# Current Context Figure Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Show how full a live session's main-conversation context is now, drop it at a compaction, colour it as a warning from 80%, and offer a button that sends `/compact` to a hosted agent.

**Architecture:** The server records a "current reading" on `SessionStats` beside the peak. Every root-stream usage reading overwrites it, and a compaction sets it to Claude's `postTokens` or marks it as awaiting Codex's next reading. Claude's `compact_boundary` record, dropped today, becomes a `ContextCompacted` event. The API exposes the reading with a window priced from the reading's own model. The web and desktop render it, and each adds a compact button on hosted sessions.

**Tech Stack:** .NET 10, TUnit, bUnit and Blazor/MudBlazor (web), Avalonia/ReactiveUI (desktop), Npgsql and KurrentDB (projections).

**Spec:** `docs/superpowers/specs/2026-11-07-current-context-figure-design.md` (in kcap). The Linear copy on AI-5296 wins where they differ.

**Repositories:**
- **kcap-server** worktree: `/Users/alexey/dev/eventstore/kcap-server/.claude/worktrees/peak-context-label`, branch `alexeyzimarev/current-context-reading` (Tasks 1–7).
- **kcap** worktree: `/Users/alexey/dev/eventstore/kcap-cli/.capacitor/worktrees/fast-fan`, branch `alexeyzimarev/ai-5296-current-context` (Tasks 8–11).
- Each worktree's `AGENTS.md`/`CLAUDE.md` rules on comments, commits and tests apply in that repo.

## Global Constraints

- The threshold is 80%, fixed. On the web, the current figure's tone is Warning from 80 and Error from 90. The peak's tone uses the same thresholds.
- Copy, verbatim:
  - Desktop labels `CONTEXT` and `PEAK CONTEXT`.
  - Web labels `Context` and `Peak context`.
  - The awaiting text is `compacted`.
  - The button's tooltip and accessible name are `Compact context`.
  - The command sent is exactly `/compact`.
  - The list tooltips start `Context: ` or `Peak context: `.
- The current reading comes only from root-stream (`AgentSession-*`) events. Nothing on an `AgentSubsession-*` stream, and no `AgentSummaryComputedEvent` merge, may change it.
- Readings overwrite and never max-merge. Echoes (`IsUsageEcho`) and readings of zero tokens are ignored.
- The Claude compaction event's id is `Guid.Parse(line.uuid)`. No other event id may change.
- Every new JSON field is optional and omitted when null or false. Older payloads and `stats_json` rows must deserialize unchanged, and older summary-event fingerprints must not move.
- A late reading or compaction never reactivates an ended session.
- The button only shows when the session is live, the agent is hosted and can be sent to, the vendor is Claude or Codex, and the current figure is at least 80%.
- The button sends through the same path as a typed message, never a command line.
- New constructor parameters and record members go second from last, not appended last.
- Linear: no kcap-server branch, title, commit message or description may contain `AI-5296`. Only the kcap PR (Tasks 8–11) carries `Closes #2481 — AI-5296`.

## Review Focus

1. **A compaction inside a subagent** (its `compact_boundary` arrives on `AgentSubsession-*`). The session's figure must not move. Pinned in Task 4.
2. **A projection rebuild**, where the root stream is replayed through usage, compaction and then `SessionSummaryComputed`. The rebuilt reading must equal the live one. Pinned in Task 3.
3. **A model switch mid-session.** The current window is priced from the current reading's model, never the peak's. Pinned in Task 5.
4. **A compact click whose send fails** (refused, a pending question, or the session ending). The button must come back rather than stay disabled. Pinned in Tasks 7 and 10.
5. **An older server**, whose summary carries no `current_context_*` fields. The desktop shows only `PEAK CONTEXT` and no button. Pinned in Task 10.

---

## Part A — kcap-server: the reading, the event, the API and the web figures (PR 1)

### Task 1: The current reading on `SessionStats`

**Files:**
- Modify: `src/Capacitor.Server.Core/SessionStatsCalculator.cs`. Add the fields to `SessionStats` after `ContextUsagePercent` (:64). In `MergeAgentSummary` (:123-148), copy the current reading back after `Aggregate`, the way `CopilotBillingFold.CopyState` does at :145.
- Create: `src/Capacitor.Server.Core/ContextReadings.cs`
- Modify: `src/Capacitor.Server.Services/ReadModels/FoldHelpers.cs`. Carry the fields in `CloneStatsOrNew` (:17-69), and add `ObserveReadingFromUsage` beside `ConsiderPeakFromUsage` (:82-88).
- Test: `test/Capacitor.Server.Core.Tests/ContextReadingsTests.cs` (create)

**Interfaces:**
- Produces, on `SessionStats` (each with `[JsonIgnore(Condition = WhenWritingNull)]`, or `WhenWritingDefault` for the bool):
  - `long? CurrentContextTokens`
  - `string? CurrentContextModel`
  - `long? CurrentContextWindow` (the window the harness reported)
  - `double? CurrentContextPercent`
  - `bool AwaitingContextReading`
- Produces, `public static class ContextReadings` in the same namespace as `SessionStats`:
  - `void Observe(SessionStats stats, long tokens, string? model, long? window)`
  - `void ObservePercent(SessionStats stats, double percent)`
  - `void ObserveCompaction(SessionStats stats, long? postTokens)`
  - `void CopyCurrent(SessionStats from, SessionStats to)`
- Produces: `FoldHelpers.ObserveReadingFromUsage(SessionStats stats, TokenUsage usage, long cacheCreation)`. It computes occupancy and window exactly as `ConsiderPeakFromUsage` does, then calls `Observe`.

- [ ] **Step 1: Write the failing tests** in `ContextReadingsTests`:
  - `A_reading_replaces_the_previous_one_even_when_lower`: observe 900_000 then 40_000 with model `"m"`. `CurrentContextTokens == 40_000`, `CurrentContextModel == "m"`.
  - `A_zero_reading_is_ignored`: observe 50_000, then 0. Still 50_000.
  - `A_compaction_with_post_tokens_sets_the_reading_and_keeps_the_window`: observe(225_521, "m", 1_000_000), then `ObserveCompaction(37_040)`. Tokens 37_040, window 1_000_000, model "m", `AwaitingContextReading == false`.
  - `A_compaction_without_counts_awaits_the_next_reading`: observe 240_674, then `ObserveCompaction(null)`. Tokens null, percent null, awaiting true.
  - `The_next_reading_after_a_compaction_clears_the_wait`: the previous case, then observe 27_296. Tokens 27_296, awaiting false.
  - `A_percent_reading_replaces_a_token_reading`: observe 50_000, then `ObservePercent(42.0)`. Percent 42, tokens null.
  - `Merging_a_subagent_summary_keeps_the_sessions_current_reading`:
    - Session stats with current 30_000 and peak 30_000, and an `AgentSummaryComputedEvent` with `PeakContextTokens = 500_000`.
    - After `MergeAgentSummary`, the peak is 500_000 and the current reading is still 30_000.
  - `Stats_without_a_reading_serialize_without_the_fields`: `JsonSerializer.Serialize(new SessionStats())` contains no `"CurrentContext"` and no `"AwaitingContextReading"`.
  - `An_older_stats_row_reads_with_no_reading`: deserializing `{"PeakContextTokens":5}` gives `CurrentContextTokens == null` and `AwaitingContextReading == false`.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Server.Core.Tests/Capacitor.Server.Core.Tests.csproj -- --treenode-filter "/*/*/ContextReadingsTests/*"`
  - Expected: a compile failure (`ContextReadings` is not defined).
- [ ] **Step 3: Implement**
  - Add the fields and `ContextReadings`.
  - `Observe` ignores `tokens <= 0`. It sets tokens and model, sets the window when `window is > 0` (otherwise null), clears the percent and clears awaiting.
  - `ObserveCompaction` with `postTokens is > 0` keeps the model and window and replaces only the tokens. Otherwise it clears tokens and percent and sets awaiting.
  - Add the `MergeAgentSummary` copy-back, the `CloneStatsOrNew` carry and `ObserveReadingFromUsage`.
- [ ] **Step 4: Run them and see them pass**
  - Run: the Step 2 command, then `--treenode-filter "/*/*/ContextMetricsStatsTests/*"`.
  - Expected: all pass. The peak tests must be unchanged.
- [ ] **Step 5: Commit**
  - `Record a session's current context reading beside its peak`

### Task 2: Claude's compaction becomes a `ContextCompacted` event

**Files:**
- Modify: `src/Capacitor.Server.Core/Events/CodexEvents.cs:87-118`. Add the fields, and update the doc comment at :78-86, which still says Claude uses continuation sessions.
- Modify: `src/Capacitor.Server.Services/Sessions/Canonical/ClaudeCodeNormalizer.cs`
  - Add a nullable `compaction` beside `command` (:47).
  - Relax the noise guard at :48.
  - Split the `"system"` arm at :71, which dereferences `command!`.
- Modify: the capture comparison in `src/Capacitor.Server.Services/Sessions/Capture/CaptureComparisonBuilder.cs:58-87`.
- Modify: `docs/ARCHITECTURE.md:259`, so it says Claude emits `ContextCompacted` too.
- Test: `test/Capacitor.Server.Tests.Ingest/Normalizers/ClaudeCompactionNormalizerTests.cs` (create). Add a case to `CodexNormalizerTests`. Add a case to the capture comparison's test class (find it by grepping `CaptureComparisonBuilder` under `test/`).

**Interfaces:**
- Produces, on `ContextCompactedEvent` (each with `[JsonIgnore(Condition = WhenWritingNull)]`):
  - `long? PreTokens` (`"pre_tokens"`)
  - `long? PostTokens` (`"post_tokens"`)
  - `string? Trigger` (`"trigger"`)
- Produces: `ClaudeCodeNormalizer` returns `new NormalizedEvent("ContextCompacted", evt, metadata, Guid.Parse(uuid))` for a `type:"system", subtype:"compact_boundary"` line that is not a sidechain.

- [ ] **Step 1: Write the failing tests**
  - `A_compaction_boundary_carries_its_counts`:
    - Input line: `{type:"system", subtype:"compact_boundary", uuid:<id>, isSidechain:false, compactMetadata:{trigger:"auto", preTokens:225521, postTokens:37040}}`.
    - Expect `EventType == "ContextCompacted"` and `EventId == id`, and a payload with `PreTokens == 225_521`, `PostTokens == 37_040`, `Trigger == "auto"`.
  - `A_boundary_without_post_tokens_still_records_the_compaction`: `compactMetadata:{trigger:"auto", preTokens:167005}` gives `PostTokens == null` and `PreTokens == 167_005`.
  - `A_sidechain_boundary_is_not_projected`: the same line with `isSidechain:true` gives null.
  - `A_manual_compaction_says_so`: `trigger:"manual"` gives `Trigger == "manual"`.
  - In `CodexNormalizerTests`, `Compacted_writes_no_counts`: serialize the payload of the existing compacted fixture; the JSON has no `"pre_tokens"`, `"post_tokens"` or `"trigger"`.
  - Capture comparison, `A_compaction_boundary_missing_from_an_older_stream_is_not_a_gap`: source lines include a `compact_boundary`, the stored stream has no `ContextCompacted`, and `RemainingGapCount == 0`.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Server.Tests.Ingest/Capacitor.Server.Tests.Ingest.csproj -- --treenode-filter "/*/*/ClaudeCompactionNormalizerTests/*"`
  - Expected: FAIL, because the result is null.
- [ ] **Step 3: Implement** the fields, the normalizer branch and the capture exclusion.
  - The exclusion skips `ContextCompacted` events re-derived from Claude lines when the stream lacks them. Sessions ingested before this change have none, and re-projecting them is a non-goal.
  - `NoiseTypes_ReturnsNull("system")` must keep passing: its line has no subtype.
- [ ] **Step 4: Run them and see them pass**
  - Run: the Step 2 command, plus `--treenode-filter "/*/*/Claude*NormalizerTests/*"`, `"/*/*/CodexNormalizerTests/*"` and `"/*/*/PiTranscriptNormalizerTests/*"`, plus the capture comparison's class.
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Project Claude's compaction boundary as a ContextCompacted event`

### Task 3: The fold keeps the reading

**Files:**
- Modify: `src/Capacitor.Server.Services/ReadModels/SessionSummaryFold.cs`
  - `ApplyTokenUsage` (:246-262) and `ApplyToolInvocation` (:226): call `FoldHelpers.ObserveReadingFromUsage` after the peak.
  - `ApplyCodexUsageBackfill` (:277): call `Observe` when `PeakCandidateTokens > 0`.
  - `ApplyAntigravityUsageBackfill` (:384): call `Observe`.
  - The Kiro block (:72-77) and `ApplyKiroUsageBackfill` (:397): call `ObservePercent`.
  - `ApplyContextObserved` (:410-423): always observe the reading, and update the peak only when `PeakWins`.
  - Add a `ContextCompactedEvent` arm that calls `ObserveCompaction(payload.PostTokens)`.
  - `ApplySummaryComputed` (:336-367): `CopyCurrent(current, fresh)`.
  - `IsReactivationEvent(object)` (:146-177): `ContextCompactedEvent => false`.
- Test: `test/Capacitor.Server.Tests.Read/ReadModels/SessionSummaryFoldTests.cs` (pure; no Docker)

**Interfaces:**
- Consumes: Task 1's `ContextReadings` and `FoldHelpers.ObserveReadingFromUsage`, and Task 2's `ContextCompactedEvent.PostTokens`.

- [ ] **Step 1: Write the failing tests**, using the file's `Apply(current, payload, ts, metadata)` helper and `CanonicalEventFactory.UsageMetadata`:
  - `A_reply_sets_the_current_reading`: an assistant text with input 1_000, cache read 200_000 and cache creation 24_155 gives `CurrentContextTokens == 225_155`.
  - `A_lower_reply_after_a_higher_one_lowers_the_reading`
  - `An_echo_leaves_the_reading_alone`: usage metadata marked as an echo leaves the previous reading in place.
  - `A_claude_compaction_drops_the_reading_to_its_post_tokens`: 225_155 followed by `ContextCompactedEvent { PostTokens = 37_040 }` gives 37_040, and the peak is unchanged.
  - `A_codex_compaction_awaits_the_next_reading`: a compaction with no counts gives `AwaitingContextReading`. A later reply clears it.
  - `A_context_observation_below_the_peak_still_sets_the_reading`: `SessionContextObservedEvent` 40_000 after a peak of 300_000 gives a current reading of 40_000 and a peak of 300_000.
  - `A_summary_event_keeps_the_current_reading`: replay a reply (225_155), a Claude compaction (37_040) and then `SessionSummaryComputedEvent` with a peak of 225_155. The reading is 37_040 (Review Focus 2).
  - `A_compaction_after_the_session_ended_does_not_reopen_it`: `SessionSummaryFold.IsReactivationEvent(new ContextCompactedEvent())` is false.
  - `A_subagent_summary_leaves_the_reading_alone`: an `AgentSummaryComputedEvent` with a larger peak leaves the current reading unchanged.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj -- --treenode-filter "/*/*/SessionSummaryFoldTests/*"`
  - Expected: the new tests fail.
- [ ] **Step 3: Implement** the fold changes listed under Files.
- [ ] **Step 4: Run them and see them pass**
  - Run: the Step 2 command, plus `--treenode-filter "/*/*/AgentSummaryFoldTests/*"`.
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Keep the current context reading in the session fold`

### Task 4: The projector's SQL path keeps the reading

**Files:**
- Modify: `src/Capacitor.Server.Services/ReadModels/SessionProjector.cs`
  - Register `OnSessionOrAgent<ContextCompactedEvent>(HandleContextCompacted)`. The new handler does `ReadStatsAsync`, then `ObserveCompaction`, then `WriteStatsAsync(..., reactivate: false, ...)`, then `FoldAndDispatchSession`, following `HandleKiroUsageBackfilled` (:1898-1915).
  - `HandleTokenUsageEvent` full path: after `AccumulateTokenUsage` (:1775), add `ObserveReadingFromUsage`.
  - `HandleSessionContextObserved` (:1945-1966): always observe, and update the peak only when `PeakWins`.
  - `HandleKiroUsageBackfilled`: add `ObservePercent`.
  - `HandleCodexUsageBackfilled` (:2152-2185): `Observe` when `PeakCandidateTokens > 0`.
  - `HandleAntigravityUsageBackfilled` and `HandleMuseUsageBackfilled`: the same observation the fold makes.
  - `HandleSummaryComputed` (:2408-2515): always read the prior stats and `CopyCurrent(prior, fresh)`.
- Modify: `src/Capacitor.Server.Services/ReadModels/SessionActivityEvents.cs`: add `ContextCompactedEvent` to `NonActivityTypes`.
- Modify: `src/Capacitor.Server.Core/ConsumeContextConverters.cs`: add an arm for `ContextCompactedEvent`.
- Test: `test/Capacitor.Server.Tests.Read/ReadModels/SessionProjectorTests.cs` (Docker Postgres)

**Interfaces:**
- Consumes: Tasks 1–3.

- [ ] **Step 1: Write the failing tests**, using the file's `Project<T>(projector, evt, stream, position, metadata)` helper and reading `stats_json` as `TokenUsage_AccumulatesInStatsJson` does:
  - `TokenUsage_SetsCurrentReading_AndALowerLaterReadingReplacesIt`
  - `ContextCompacted_FromClaude_SetsReadingToPostTokens`
  - `ContextCompacted_WithoutCounts_AwaitsTheNextReading`
  - `SummaryComputed_KeepsTheCurrentReading`
  - `ContextObserved_BelowThePeak_StillSetsTheReading`
  - `A_subagent_compaction_leaves_the_session_reading_alone`: project `ContextCompactedEvent { PostTokens = 1_000 }` on `AgentSubsession-x-agent1` after a root reading of 200_000. The root row still reads 200_000 (Review Focus 1).
  - `ContextCompacted_AfterTheSessionEnded_DoesNotReopenIt`: the row's `status` and `ended_at` are unchanged.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Server.Tests.Read/Capacitor.Server.Tests.Read.csproj -- --treenode-filter "/*/*/SessionProjectorTests/*"` (needs Docker).
  - Expected: the new tests fail.
- [ ] **Step 3: Implement** the projector, classification and converter changes.
- [ ] **Step 4: Run them and see them pass**
  - Run: the Step 2 command, plus `--treenode-filter "/*/*/ContextBackfillProjectorTests/*"` and `"/*/*/CodexUsageBackfillProjectorTests/*"`.
  - Expected: all pass. Constructing the projector also proves `AssertClassificationMatches`.
- [ ] **Step 5: Commit**
  - `Project the current context reading into session stats`

### Task 5: The API carries the reading

**Files:**
- Modify: `src/Capacitor.Api.Public.Abstractions/Sessions/ActivitySessionStats.cs`. Add the same five fields, in the same position and with the same `JsonIgnore` as on `SessionStats`. JSON names `current_context_tokens`, `current_context_model`, `current_context_window`, `current_context_percent`, `awaiting_context_reading`.
- Modify: `src/Capacitor.Api.Public/Sessions/ActivitySessionMapping.cs:82-132`. Map them.
- Modify: `src/Capacitor.Api.Public/Sessions/SessionUsageEstimate.cs:9-30`. `Decorate` replaces `CurrentContextWindow` with `UsagePricing.ContextWindow(pricing, vendor, stats.CurrentContextWindow, stats.CurrentContextModel)`.
- Modify: `src/Capacitor.Server.Core/Sessions/SessionResponseShaper.cs:192-220`. `ShapeStats` copies the fields.
- Modify: `src/Capacitor.Api.Web.Abstractions/Sessions/SessionStatsView.cs`. Add optional `init` properties (not `required`): `long? CurrentContextTokens`, `long? CurrentContextWindow` (resolved), `double? CurrentContextPercent`, `bool AwaitingContextReading`.
- Modify: `src/Capacitor.Api.Web/Sessions/SessionSummaryMapper.cs:74-112`. Fill them, with `CurrentContextWindow = UsagePricing.ContextWindow(pricing, vendor, s.CurrentContextWindow, s.CurrentContextModel)`.
- Test:
  - `test/Capacitor.Server.Tests.Read/Sessions/ActivitySessionWireShapeTests.cs`: add the fields to `FullStats()`.
  - `test/Capacitor.Api.Public.Tests/Sessions/SessionUsageEstimateTests.cs`
  - `test/Capacitor.Api.Web.Tests/Sessions/SessionsWebServiceReadTests.cs`: add them to `FullStats`/`FullStatsView`.
  - `test/Capacitor.Server.Core.Tests/Sessions/SessionResponseShaperTests.cs`

**Interfaces:**
- Produces: the public `/api/sessions/{id}/summary` stats carry `current_context_tokens`, `current_context_window` (resolved), `current_context_percent` and `awaiting_context_reading` (Task 8 consumes these). The web reads `SessionStatsView.CurrentContext*` (Tasks 6 and 7).

- [ ] **Step 1: Write the failing tests**
  - Extend the wire-shape `FullStats()` with a current reading. The existing equality test must stay green after the change.
  - `The_current_window_is_priced_from_the_current_model` (Review Focus 3):
    - The peak model `"opus-a"` has a window of 200_000 and the current model `"opus-b"` has 1_000_000 in the stub pricing.
    - The decorated `CurrentContextWindow == 1_000_000` and `ContextWindow == 200_000`.
  - `A_reported_current_window_wins_over_the_price`.
  - `ShapeStats_keeps_the_current_reading`.
  - The web read test: the view carries `CurrentContextTokens` and the resolved `CurrentContextWindow`.
- [ ] **Step 2: Run them and see them fail**
  - Run each class with `dotnet run --project <proj> -- --treenode-filter "/*/*/<Class>/*"`.
  - Expected: compile failures or mismatches.
- [ ] **Step 3: Implement** the mappings.
- [ ] **Step 4: Run them and see them pass**
  - Run: the same commands.
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Expose the current context reading on session summaries`

### Task 6: Web figures, list meter and chains

**Files:**
- Modify: `src/Capacitor.Ui/Components/Pages/Sessions/FigureKind.cs`: add `CurrentContext`.
- Modify: `src/Capacitor.Ui/Components/Pages/Sessions/SessionFigures.cs`
  - Add the current figure before the peak.
  - Thresholds: `ContextTone` becomes Warning from 80 and Error from 90.
  - The current figure is Neutral when `!isActive`.
  - The peak is Neutral when a current reading exists.
- Modify: `src/Capacitor.Ui/Components/Sessions/SessionStatsAggregator.cs:10-47`. Take the current fields from the last input only, outside `SessionTotals`.
- Modify: `src/Capacitor.Ui/Components/Pages/Sessions/SessionsPage.razor:1041-1049`: `GetContextPercent` and `GetContextDisplay` prefer the current reading.
- Modify: `SessionsList.razor:439`: `ContextTip = ContextDisplay(meta)`.
- Modify: `SessionListRow.razor:44`: meter `low="80"`.
- Modify: the `ContextDisplay`/`ContextPercent` parameter docs (:297-301).
- Test:
  - `test/Capacitor.Ui.Tests/SessionFiguresTests.cs`
  - `test/Capacitor.Ui.Tests/SessionStatsAggregatorTests.cs`
  - `test/Capacitor.Ui.Tests/SessionsListRowTests.cs`
  - The `SessionViews` helper gains a current-reading builder.

**Interfaces:**
- Produces, in `SessionFigures`:
  - `public const double CompactThreshold = 80;`
  - `public static double? CurrentContextPercent(SessionStatsView stats)`: `CurrentContextPercent ?? (tokens > 0 && window > 0 ? 100 * tokens / window : null)`.
  - `public static bool HasCurrentReading(SessionStatsView stats)`: tokens or percent present, or awaiting.
  - The figure for `FigureKind.CurrentContext` is labelled `Context`. Its value is `EventStyles.FormatContext(tokens, window, percent)`, or `compacted` while awaiting.

- [ ] **Step 1: Write the failing tests**
  - `A_live_session_shows_its_current_context_before_the_peak`:
    - The labels end `…, "Context", "Peak context"`.
    - The current value is `"37k / 1M (4%)"` for tokens 37_040 and window 1_000_000.
  - `The_current_figure_warns_from_80_percent`, an `[Arguments]` table over a window of 100_000: tokens 79_000 gives Neutral, 80_000 gives Warning, 90_000 gives Error.
  - `An_ended_sessions_current_figure_is_neutral`
  - `With_a_current_reading_the_peak_is_neutral`
  - `A_session_awaiting_a_reading_says_compacted`
  - `Without_a_current_reading_only_the_peak_shows`
  - The existing `Parts_carry_the_tone_of_what_they_state` (Warning at 88%) stays green.
  - `The_current_reading_is_the_newest_sessions`: two members, older with 900_000 and newer with 30_000, give 30_000.
  - `A_newest_session_without_a_reading_shows_none`: the older member has a reading, the newest has none, and the aggregate has none.
  - List row, `The_row_tooltip_names_the_current_context`:
    - Stub `ContextDisplay => "Context: 37k / 1M (4%)"`; the tooltip renders that text.
    - The meter's `low` attribute is `"80"`.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Ui.Tests/Capacitor.Ui.Tests.csproj -- --treenode-filter "/*/*/SessionFiguresTests/*"`, and the same for the other two classes.
  - Expected: the new tests fail.
- [ ] **Step 3: Implement** the changes listed under Files.
- [ ] **Step 4: Run them and see them pass**
  - Run: the full `Capacitor.Ui.Tests` project, which includes the stylesheet pins, then `test/Capacitor.Source.Tests`.
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Show the current context figure on sessions`
- [ ] **Step 6: Open PR 1**
  - Push over HTTPS. Use the template's three headings.
  - The reference line is `Part of kurrent-io/kcap#2481`, with no Linear id. The Verification section names each changed test, its layer, and what was broken to see it fail.

## Part B — kcap-server: the agent page's figure and compact button (PR 2)

### Task 7: Compact from the agent page

**Files:**
- Create: `src/Capacitor.Ui/Components/Pages/Agents/AgentContextFigure.razor`
- Modify: `src/Capacitor.Ui/Components/Pages/Agents/AgentDetailPage.razor`: mount it beside `AgentStatusLine` (:78-80).
- Modify: `AgentDetailPage.Live.cs`: add `CompactAsync` and the gate.
- Modify: `src/Capacitor.Ui/wwwroot/css/kit.css`: add a `.k-icon-btn-warn` modelled on `.k-icon-btn-ok` (:731-806), using the existing warning token.
- Modify: `docs/testing/FLOWS.md`: add the next free `AGT-` flow.
- Test:
  - `test/Capacitor.Ui.Tests/AgentContextFigureTests.cs` (create, bUnit; harness `SessionDetailPageHarness`, as `AgentPageFactsTests` uses it).
  - `test/Capacitor.Server.Tests.UI/SmokeTests/HostedAgentStories.cs`: a new `[Flow]` beside AGT-03.

**Interfaces:**
- Consumes: Task 6's `SessionFigures.CurrentContextPercent`, `HasCurrentReading` and `CompactThreshold`, and `EventStyles.FormatContext`.
- Produces: `AgentContextFigure` parameters:
  - `SessionStatsView? Stats`
  - `bool Live`
  - `bool CanCompact`
  - `Func<Task<bool>> Compact`
- The page's gate is `CanWrite && IsRunning && ClientConnected && !IsFlowParticipant && _live is not null && vendor is Vendor.Claude or Vendor.Codex`.
- `CompactAsync` sends `_live.SendInputAsync(new ChatMessage("/compact"))`:
  - `Applied` or `Held` returns true.
  - `QuestionPending` shows a snackbar, `Answer the agent's question first, then compact`, and returns false.
  - Anything else returns false.

- [ ] **Step 1: Write the failing tests**
  - `A_running_agent_over_80_percent_offers_to_compact`: there is a button whose `aria-label` is `Compact context`.
  - `The_button_sends_compact_through_the_agent_input`: `harness.AgentControl.Received(1).SendUserInputAsync(agentId, "/compact", Arg.Any<string[]?>())`.
  - No button in each of these cases:
    - `No_button_below_80_percent`
    - `No_button_for_a_viewer_without_write_access`
    - `No_button_for_a_flow_participant`
    - `No_button_for_an_unsupported_vendor`
    - `No_button_once_the_agent_ended`
  - `After_a_click_the_button_waits_for_the_compaction`: the button is disabled after the click. Re-rendering with stats awaiting a reading enables it again once a later reading is below 80.
  - `A_refused_send_gives_the_button_back` (Review Focus 4): the stub returns `QuestionPending`, and the button is enabled with the snackbar text shown.
  - Flow `A_member_compacts_their_running_agent`, modelled on AGT-03: the daemon receives `/compact`.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Ui.Tests/Capacitor.Ui.Tests.csproj -- --treenode-filter "/*/*/AgentContextFigureTests/*"`
  - Expected: compile failure.
- [ ] **Step 3: Implement**
  - The button is a native `<button class="k-icon-btn k-icon-btn-warn">` inside `<MudTooltip Text="Compact context">`, with `aria-label="Compact context"` and `<MudIcon Icon="@Icons.Material.Filled.Compress">`. Copy `ChatComposer.razor:49-63`.
  - The figure's value uses the `sd-ov-fig-warning` tone class from 80% while `Live`.
- [ ] **Step 4: Run them and see them pass**
  - Run: the Step 2 command; the full `Capacitor.Ui.Tests`; `test/Capacitor.Source.Tests`; the new flow (`Capacitor.Server.Tests.UI`, which needs Docker).
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Offer to compact a running agent from its page`
- [ ] **Step 6: Open PR 2**
  - Use the same reference-line rule as PR 1: no Linear id.

## Part C — kcap: the desktop row and button (PR 3)

### Task 8: The desktop reads the reading

**Files:**
- Modify: `src/Capacitor.Cli.Core/WorkItems/SessionStatsDto.cs`: add the fields before `EstimatedCostUsd`.
- Modify: `src/Capacitor.App/ViewModels/SessionFactText.cs`: add the current-context helpers.
- Test:
  - `test/Capacitor.Cli.Core.Tests.Unit/WorkItems/WorkContextDtoTests.cs`
  - `test/Capacitor.App.Tests.Unit/SessionFactTextTests.cs`

**Interfaces:**
- Produces, on `SessionStatsDto`:
  - `long? CurrentContextTokens` (`current_context_tokens`)
  - `long? CurrentContextWindow` (`current_context_window`)
  - `double? CurrentContextPercent` (`current_context_percent`)
  - `bool AwaitingContextReading` (`awaiting_context_reading`)
- Produces, on `SessionFactText`:
  - `public static string? CurrentContext(SessionStatsDto? stats)`: null when there is no reading and no wait; `"compacted"` when awaiting; otherwise the `Context` format.
  - `public static double? CurrentContextPercent(SessionStatsDto? stats)`

- [ ] **Step 1: Write the failing tests**
  - `Summary_reads_the_current_context_reading`: a raw body with `"current_context_tokens":37040,"current_context_window":1000000,"awaiting_context_reading":false` deserializes to those values.
  - `An_older_summary_has_no_current_reading`: the existing body gives null and false.
  - `CurrentContext` table:
    - (37_040, 1_000_000, null, false) gives `"37k / 1M (4%)"`.
    - (null, null, null, true) gives `"compacted"`.
    - (null, null, 42.0, false) gives `"42% ctx"`.
    - (null, null, null, false) gives null.
- [ ] **Step 2: Run them and see them fail**
  - Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/WorkContextDtoTests/*"` and the App class, with the `KCAP_*` variables unset.
  - Expected: compile failure.
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run them and see them pass**
  - Run: the same commands, plus `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'`.
  - Expected: the tests pass and the grep is empty.
- [ ] **Step 5: Commit**
  - `Read the current context reading in the desktop app (#2481)`

### Task 9: Send text from outside the composer

**Files:**
- Modify: `src/Capacitor.App/ViewModels/ChatTabViewModel.cs`
- Test: `test/Capacitor.App.Tests.Unit/ChatComposerTests.cs`

**Interfaces:**
- Produces:
  - `public bool CanSendText { get; }`, raised on change. Its gate is `canRun`'s at :1066-1071 plus `!HasTerminalMenu && CanSendBeforeSeed`.
  - `public Task<bool> SendTextAsync(string text)`, which calls `SendComposerAsync(text, includeTray: false, bypassRecall: true, ownsDraft: false)`. It returns true when the send was accepted.

- [ ] **Step 1: Write the failing tests**, built with `BuildAttachedAsync()`:
  - `Sending_text_from_outside_the_composer_pastes_it_and_keeps_the_draft`: with `ComposerText = "draft"`, `SendTextAsync("/compact")` gives `client.SentInput[0] == TerminalInputEncoder.Paste("/compact")`, and `ComposerText == "draft"`.
  - `Sending_text_is_refused_once_the_session_has_ended`: copy `Running_a_code_block_is_refused_once_the_session_has_ended`. The result is false and nothing is sent.
- [ ] **Step 2: Run them and see them fail**
  - Run: the App tests with `--treenode-filter "/*/*/ChatComposerTests/*"` and `KCAP_*` unset.
  - Expected: compile failure.
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run them and see them pass**
  - Run: the same command.
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Let the desktop send a message without touching the composer draft (#2481)`

### Task 10: The work-context pane's reading, warning and compact command

**Files:**
- Modify: `src/Capacitor.App/ViewModels/WorkContextViewModel.cs` and `WorkContextViewModel.Projections.cs` (`ApplySessionFacts`, :420-434).
- Modify: `src/Capacitor.App/ViewModels/WorkspaceViewModel.cs:245-255, 387-388`.
- Modify: `src/Capacitor.App/ViewModels/RemoteSessionViewModel.cs:326-340, 387-388, 572-579`. Add `AwaitingInput: _row.AwaitingInput` to `PublishWorkPresence`.
- Test: `test/Capacitor.App.Tests.Unit/WorkContextViewModelTests.cs`

**Interfaces:**
- Consumes: Task 8's `SessionFactText.CurrentContext` and `CurrentContextPercent`, and Task 9's `ChatTabViewModel.CanSendText` and `SendTextAsync`.
- Produces, on `WorkContextViewModel`:
  - `string? CurrentContextUse`
  - `bool ShowsCurrentContext`
  - `bool CurrentContextWarns`
  - `bool ShowsCompact`
  - `ReactiveCommand<Unit, Unit> CompactCommand`
  - `internal void AgentInputAvailabilityChanged()`
  - `internal const double CompactThreshold = 80;`
  - `internal static readonly TimeSpan CompactPollInterval = TimeSpan.FromSeconds(3);`
  - `internal static readonly TimeSpan CompactPollLimit = TimeSpan.FromMinutes(2);`
- New constructor parameters, inserted second from last: `Func<bool> canSendToAgent`, `Func<string, Task<bool>> sendToAgent`. The workspaces pass:
  - `() => Chat is { ShowsComposer: true, CanSendText: true }`
  - `text => Chat?.SendTextAsync(text) ?? Task.FromResult(false)`
  - They call `AgentInputAvailabilityChanged()` from the existing `Chat.WhenAnyValue(...)` subscriptions.
- Rules:
  - Live means `_dto` is not null and `!SessionStatusDots.IsTerminal(_dto.Status)`.
  - `CurrentContextWarns = live && percent >= 80`.
  - `ShowsCompact = CurrentContextWarns && !awaiting && !_compactPending && canSendToAgent()`, with the vendor Claude or Codex, compared case-insensitively.
  - A click sends `/compact`. On true it sets `_compactPending` and switches the timer to `CompactPollInterval`.
  - A summary that is awaiting, or below 80, or `CompactPollLimit` passing, restores `PollInterval` and clears the pending flag. A false result clears the pending flag at once.
  - `OnDto` re-reads the pane (`_reader.Refresh()`) when `AwaitingInput` flips to true or `Status` changes.

- [ ] **Step 1: Write the failing tests** (Harness, `Source.Requested.Count`, `FakeTimeProvider`):
  - `A_summary_with_a_current_reading_fills_the_context_row`: `CurrentContextUse == "37k / 1M (4%)"`, `ShowsCurrentContext`.
  - `The_context_row_warns_from_80_percent_on_a_live_session`: 79% gives false and 80% gives true.
  - `An_ended_session_never_warns_or_offers_to_compact`
  - `Compact_is_offered_only_when_the_agent_can_be_sent_to`: with `canSendToAgent` false there is no `ShowsCompact`.
  - `Compact_sends_slash_compact_and_polls_until_the_drop`:
    - Executing the command sends exactly `"/compact"`.
    - Advancing 3 s triggers a read.
    - A summary below 80% restores the 30 s cadence.
  - `A_refused_compact_offers_the_button_again` (Review Focus 4)
  - `A_turn_end_rereads_the_summary`: pushing a dto with `AwaitingInput = true` raises `Source.Requested.Count` by one.
  - `An_older_server_without_current_fields_shows_only_the_peak` (Review Focus 5): `ShowsCurrentContext` is false and `ShowsCompact` is false.
- [ ] **Step 2: Run them and see them fail**
  - Run: the App tests with `--treenode-filter "/*/*/WorkContextViewModelTests/*"` and `KCAP_*` unset.
  - Expected: the new tests fail.
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run them and see them pass**
  - Run: the same command, plus `"/*/*/WorkspaceViewModelTests/*"` and `"/*/*/RemoteSessionViewModelTests/*"`.
  - Expected: all pass.
- [ ] **Step 5: Commit**
  - `Track the current context and offer to compact in the session pane (#2481)`

### Task 11: The row and the button in the view

**Files:**
- Modify: `src/Capacitor.App/Views/WorkContextView.axaml`
  - Change the `SessionUsage` grid at :889 to four rows, with CONTEXT at row 2 and PEAK CONTEXT at row 3.
  - CONTEXT's value cell is a `Grid ColumnDefinitions="*,Auto"` holding a `SelectableTextBlock.factValue`, with `Classes.warning="{Binding CurrentContextWarns}"`, and the button.
  - The row is visible when `ShowsCurrentContext` is true.
  - Add a `SelectableTextBlock.factValue.warning { Foreground = KcapWarningBrush }` style inside the `SessionFactSections` styles (:851-875).
- Modify: the named icon mapping. If the chat's compaction row already has an icon, add the semantic member there; otherwise add `CompactIcon => LucideIconKind.Shrink` to `src/Capacitor.App/Views/ToolCategoryIcons.cs`.
- Test: `test/Capacitor.App.Tests.Unit/ButtonAutomationNameTests.cs:105-185`. Add `Compact context` to the expected list.
- Modify: `test/Capacitor.App.Tests.Unit/` usage-grid smoke expectations, if a test pins the row count.

**Interfaces:**
- Consumes: Task 10's view model members.
- The button is `Classes="kcapGhost kcapIcon"`, `Padding="5" MinHeight="0"`, with `ToolTip.Tip="Compact context"`, `AutomationProperties.Name="Compact context"` and `Command="{Binding CompactCommand}"`. It is visible when `ShowsCompact` is true. It holds `<views:AppIcon Kind="{x:Static views:ToolCategoryIcons.CompactIcon}" Size="12" Foreground="{StaticResource KcapWarningBrush}"/>`. Copy `CommandChip.axaml:15-22`.

- [ ] **Step 1: Write the failing test**: extend `The_work_context_pane_names_every_section_and_row` with `Compact context`, for a view model that shows the button.
- [ ] **Step 2: Run it and see it fail**
  - Run: the App tests with `--treenode-filter "/*/*/ButtonAutomationNameTests/*"`.
  - Expected: FAIL, because the button is missing.
- [ ] **Step 3: Implement** the XAML and the icon mapping.
- [ ] **Step 4: Run and verify**
  - Run: the full App suite with `KCAP_*` unset, and a full rebuild of `src/Capacitor.App` (`dotnet build src/Capacitor.App/Capacitor.App.csproj --no-incremental`).
  - Expected: the suite passes, and the build has no AVLN warnings.
- [ ] **Step 5: Commit**
  - `Show the current context row and compact button (#2481)`
- [ ] **Step 6: Open PR 3**
  - The branch carries the spec and this plan.
  - The reference line is `Closes #2481 — AI-5296`.
  - Open it only after PR 1 is merged and deployed, since the desktop needs the server fields to show anything; it still degrades to the peak without them.
