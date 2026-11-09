# Daemon Recording and Terminal Steering Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

> **Execution:** subagent-driven, by the owner's choice. The owner lifted the hold on 2026-11-06, and agreed that the FIFO matcher stays for raw terminal typing and to the two columns in Task 17.

**Goal:** Move transcript recording into the daemon, give remote messages an exact author, and let a person steer their own running terminal Claude session without moving it.

**Architecture:** The watcher's logic moves into a shared `Capacitor.Cli.Recording` assembly that `kcap watch` and the daemon both run. The daemon stamps rows it can place from a persisted input log. Terminal sessions are steered by a Claude Code mod that talks to the daemon and reports which row each message became.

**Tech Stack:** .NET 10 NativeAOT, SignalR, TUnit, event-sourced server (Eventuous), Claude Code mod API (TypeScript).

**Spec:** `docs/superpowers/specs/2026-11-05-daemon-recording-and-terminal-steering-design.md`. Read it before any task. Its "Agreed decisions" and failure tables are requirements.

## Global Constraints

- Two repositories: `kcap-cli` (this one) and `kcap-server`. The server pins the CLI as the `src/cli` submodule.
- Wire changes land on the server first, as optional trailing fields. An older CLI or daemon must keep today's path.
- `FrameType` values are append-only, and each new frame has a live handler before its capability joins `LocalControlCapabilities.Current`.
- Transcript event ids don't change. `TranscriptIds` is a persistence contract.
- No new Postgres tables. Claims, the input log and the steering switch are daemon files, stamps are event metadata, attribution stays in `hosted_agent_input_attribution`, and dispatch states are events on `HostedAgentInput-{agentId}`. The only schema change is two nullable columns on `hosted_agent_pending_dispatches`, in Task 17.
- Read agent-owned files with `FileShare.ReadWrite`.
- A redaction timeout holds its source, and never drops or marks the line.
- A 401 spools, and only a rejected payload drops. Status classification goes through `HookSpool.IsRetryable`.
- The PTY read loop never awaits a consumer.
- The daemon never stops a process in its own group.
- Run `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` before each PR. Any output fails it.
- New projects have no `InternalsVisibleTo` to a production assembly. Tests get a grant.
- One test project per production project, mirroring its directories. Throwaway paths use Helpers' `TempDir`.
- Comments follow `CLAUDE.md`: scarce, no ticket ids, no history.
- Commit subjects are one imperative clause of at most 80 characters. AI-4410 has no GitHub twin, so add a reference only when one exists, and ask rather than invent one.
- Any change to CLI surface updates `README.md` in the same PR.

## Review Focus

1. **A transcript shrinks or is rewritten under the recorder.** A vendor compacts or rotates its file. The recorder must notice the file is shorter than its byte offset and recover through the server's position. Task 1.
2. **A laptop sleeps.** The daemon's heartbeat goes stale, a hook starts a watcher, and the daemon wakes. The daemon must see it lost the claim and stop sending that source. Task 8.
3. **Two profiles on one machine.** A hook on profile B with only profile A's daemon running must spawn a watcher, and A's daemon must never take the session. Task 15.
4. **A hook fires during launch.** A hosted agent's first hook can run before anything else has happened. The claim must exist before the agent process starts. Task 9.
5. **One session in two processes.** `--resume` in a second terminal while the first is alive means two mods attach for one session id. The second is refused and the first keeps the attachment. Task 18.

---

## How to read the tasks

Each task lists its tests, signatures and commit subject. The cycle is the same every time: write the tests, run them and see them fail, implement, run them and see them pass, then commit. Test cases use TUnit (`await Assert.That(actual).IsEqualTo(expected)`). In kcap-cli run one class with:

```bash
dotnet run --project test/<Project>/<Project>.csproj -- --treenode-filter "/*/*/<ClassName>/*"
```

In kcap-server use the same form against the test project each task names. Each milestone is one PR per repository unless a task says otherwise.

## Shared names

These are fixed here so every task uses the same ones.

```csharp
// Capacitor.Cli.Recording
public sealed record RecordingSource(string SessionId, string? AgentId, string TranscriptPath, string Vendor, string? Cwd) {
    public string Key => AgentId is null ? SessionId : $"{SessionId}-{AgentId}";
}
public interface IRecorderTransport {
    bool IsConnected { get; }
    Task<int> ConnectAsync(RecordingSource source, CancellationToken ct);       // WatcherConnect
    Task<int> SendBatchAsync(TranscriptBatch batch, CancellationToken ct);      // Cursor: SendTranscriptBatchAcked, which returns the next line. The rest: SendTranscriptBatch2
    Task SendTitleAsync(string sessionId, string title, CancellationToken ct);  // SendTitle
    Task UpdateTitleAsync(string sessionId, string title, string? model, long inputTokens, long outputTokens, long cacheReadTokens, long cacheWriteTokens, CancellationToken ct); // UpdateTitle
    Task DrainCompleteAsync(RecordingSource source, CancellationToken ct);      // WatcherDrainComplete
    event Func<string, string, Task> StopRequested;                             // key, reason
    event Func<Task> Reconnected;                                               // each recorder re-registers and rewinds
}
public sealed record RecorderContext(CapacitorServer Server, IRecorderHousekeeping Housekeeping, TimeProvider RedactionClock, Action<string> Log, Action Beat);
public interface IRowStamper {
    LineStamp? StampFor(int lineNumber, string rawLine);
    void Acknowledged(int throughLine);
}

// Wire, on both sides, optional on TranscriptBatch as LineStamp[]? Stamps
public sealed record LineStamp(int LineNumber, Guid? DispatchId, bool IsLocal);

// Capacitor.Cli.Core
public enum RecorderClaimState { None, DaemonFresh, DaemonStale }
```

---

## Milestone 1: the watcher stops re-reading (AI-3706)

`TranscriptLineIndex` (#1449) already gives every vendor a byte position: a poll seeks to its line cursor and reads a bounded tail, `WaitForFinalLineCompletionAsync` reads only the final line, and `SpoolUndeliveredTranscriptTailAsync` takes the index. A live watcher on a 15 MB transcript idles at 0.1 to 0.2% CPU, where the issue measured 5 to 7% at that size. This milestone keeps that design and closes what's left: a shrink, and tests that pin the costs.

### Task 1: Keep the line cursor when a transcript shrinks

**Files:**
- Modify: `src/Capacitor.Cli/Commands/WatchCommand.cs` (`DrainNewLines`)
- Test: create `test/Capacitor.Cli.Tests.Unit/Commands/TranscriptShrinkDrainTests.cs`; modify `test/Capacitor.Cli.Tests.Unit/Commands/IndexedTranscriptReadTests.cs`

**Interfaces:**
- Produces: a read never lowers `state.LinesProcessed`. Only a reconnect rewind does. `TranscriptLineIndex` stays the tail position for every vendor but Cursor, and `CursorByteOffset` stays Cursor's.

- [ ] **Tests:**
  - `A_file_shorter_than_the_cursor_keeps_the_cursor`: cursor at 10, file truncated to 4 lines. A drain sends nothing and `LinesProcessed` stays 10.
  - `Lines_past_the_cursor_after_a_shrink_keep_their_own_numbers`: the same file then grows to 13 lines, and the next batch holds exactly line numbers 10, 11 and 12, the server's next line onward.
  - `A_shrink_is_logged_once`: three drains on the shrunk file log one line.
  - `Reconnect_rewind_resends_exactly_the_gap`: the server is 3 lines behind, and exactly those 3 lines go out with the same line numbers as before.
  - `A_line_cut_inside_a_multibyte_character_is_held_until_complete`, with an index, in `IndexedTranscriptReadTests`.
- [ ] **Implement:** every path that sets `LinesProcessed` from a read takes the larger of the cursor and the read's position. A shrink logs once, and the flag clears when the file reaches the cursor again. Cursor keeps its quarantine. Lines written below the cursor after a shrink stay out of the live stream, because the server would drop them. Nothing sends them later: `kcap import` resumes from the server's cursor too, and `kcap import --repair-capture` restores only an ended Claude or Codex session's missing file-tool records.
- [ ] **Verify:** both classes pass, plus `ReadNewCompleteLinesAsyncTests`, `CursorReconnectRewindTests`, `HeldLineDrainTests`, `ShortLiveSessionTests` and `WatchCommandTests`.
- [ ] **Commit:** `Keep a watcher's line cursor when its transcript shrinks`

### Task 2: Pin the tail costs and measure them

**Files:**
- Modify: `src/Capacitor.Cli/Commands/WatchCommand.cs` (`ReadFinalLineSharedAsync`), only to let a test count its bytes
- Test: `test/Capacitor.Cli.Tests.Unit/Commands/IndexedTranscriptReadTests.cs`

**Interfaces:**
- Consumes: `TranscriptLineIndex`, and the test class's `CountingFileStream`.
- Produces: `internal static Task<string> ReadFinalLineAsync(FileStream stream, string path)`, which `ReadFinalLineSharedAsync` calls.

- [ ] **Tests:**
  - `The_final_wait_reads_only_the_tail`: a 10 MB file, with bytes read under 64 KiB.
  - `Spooling_the_tail_reads_only_the_tail`: a `ConsumeIfComplete` read with an index on a 10 MB file, with bytes read after the first pass under 64 KiB.
  - `A_resume_reads_the_file_once`: a first read at a high cursor reads the file, and the second reads under 64 KiB.
- [ ] **Implement:** the seam only. These pin behaviour that already holds, so a failure means the plan's reading of the code is wrong: report it rather than change the read.
- [ ] **Verify:** with a throwaway harness that isn't committed, run 60 idle polls over a 100 MB transcript with an index and report CPU per poll from `Process.TotalProcessorTime`. The number goes in the PR.
- [ ] **Commit:** `Pin the watcher's tail read costs`

---

## Milestone 2: steering proof (throwaway, gate G1)

### Task 3: Prove the mod path with real processes

Nothing here merges. Build it outside the repository, in a scratch directory.

**Files:**
- Create (scratch): `steering-proof/plugin/` (an installed-style plugin), `steering-proof/stub-daemon.mjs` (HTTP on a Unix socket), `steering-proof/log.ndjson`

- [ ] **Build:** a Node stub standing in for daemon and server, with `/attach`, `/next`, `/link` and `/undelivered`, and a mod with the spike's `session.append` link logic, submitting from `$.clock.every`. Install it with `/plugin install` from a local marketplace, not the dev-mods folder.
- [ ] **Run, and record each outcome in the log:**

| Case | What to record |
|---|---|
| Round trip | Row uuid in the link equals the uuid on the transcript line |
| Esc during a turn with a prompt queued | Row, drop, or silence |
| Exit with a prompt queued | Row, drop, or silence |
| Compaction with a prompt queued | Row, and whether it's duplicated |
| `/clear` with a prompt queued | Whether it lands in the new session, and whether the mod's state survives |
| Resume with a prompt queued | The same |
| Stub killed between push and handover | Nothing submitted |
| Row arriving after the mod reported undelivered | The late link |
| Module reload with a dispatch outstanding | Whether it's remembered |
| `socketPath` on Windows, in the VM | Works or not |
| A second process on one session | What the mod sees |

- [ ] **Decide G1 from the spec's criteria:**
  - **Pass:** every dispatch ends in the state the tables give, a Rejected dispatch never produces a row, no dispatch produces two rows, and Unconfirmed is rare. No row from an old session can be linked to a dispatch sent after `/clear` or resume.
  - **Fail:** Unconfirmed is routine. Stop Milestone 6 and bring it back to the owner.
- [ ] **Record:** add the results to the spec's "What the spike proved", including where the mod keeps its outstanding dispatch and whether `/clear` always discards the queue. If it does, take the spec's relaxed path for that case in Tasks 17 and 20. Commit the spec change alone: `Record the steering proof's results`.

---

## Milestone 3: extract the recorder (no behaviour change)

### Task 4: Create the recording assembly

**Files:**
- Create: `src/Capacitor.Cli.Recording/Capacitor.Cli.Recording.csproj`, `test/Capacitor.Cli.Recording.Tests.Unit/`
- Move: `src/Capacitor.Cli/Capture/*` to `src/Capacitor.Cli.Recording/Capture/`, and `src/Capacitor.Cli/Commands/TranscriptBatchBuffer.cs`, `src/Capacitor.Cli/SecretRedactor.cs` and `src/Capacitor.Cli/SecretPatterns.cs` to `src/Capacitor.Cli.Recording/`, with their tests. `Capture` and the redactor depend on each other, so they move together
- Move: the shared part of `test/Capacitor.Cli.Tests.Unit/WatcherPatternWarmup.cs` to `test/Capacitor.Tests.Helpers/`, because both test assemblies redact and each needs its own `[Before(Assembly)]` hook
- Modify: `src/Capacitor.Cli/Capacitor.Cli.csproj`, `Capacitor.slnx`

**Interfaces:**
- Produces: project `Capacitor.Cli.Recording`, referencing `Capacitor.Cli.Core` and `Capacitor.Models.Transcripts`, referenced by `kcap` and later `kcap-daemon`. It references neither executable.

- [ ] **Tests:** moved tests pass unchanged apart from namespaces.
- [ ] **Implement:** move files, fix namespaces, and make needed members public rather than granting `InternalsVisibleTo`.
- [ ] **Verify:** `dotnet build Capacitor.slnx`, the moved suite, and the AOT publish check.
- [ ] **Commit:** `Move transcript capture into a recording assembly`

### Task 5: Extract the per-source recorder

**Files:**
- Create: `src/Capacitor.Cli.Recording/TranscriptRecorder.cs`, `RecordingSource.cs`, `RecorderContext.cs`, `IRecorderTransport.cs`, `IRecorderHousekeeping.cs`, `IRowStamper.cs`, `NoStamps.cs`
- Create: `src/Capacitor.Cli/Commands/HubRecorderTransport.cs`
- Modify: `src/Capacitor.Cli/Commands/WatchCommand.cs`
- Move: `CursorRewriteGuard`, and the pure line helpers `TitleGenerator.IsCodexInjectedUserPrelude`, `TitleGenerator.ExtractCodexBlockText`, `GeminiImportSource.IsImportRelevantLine` and `CopilotFinalizeDrainCommand.IsShutdownLine`, so the line extractors move whole and `IRecorderHousekeeping` starts small
- Move: the tests that exercise the moved logic

**Interfaces:**
- Consumes: Task 4.
- Produces: `TranscriptRecorder(RecordingSource source, IRecorderTransport transport, IRowStamper stamper, TimeProvider time, RecorderContext context)` with `Task RunAsync(CancellationToken ct)` and `Task FinishAsync(CancellationToken ct)`. Every recorder has a full context: there is no reduced mode without one, and a test builds one from a `TempDir`. `IRecorderHousekeeping` holds what stays in `kcap` for now, with housekeeping behind four coarse calls, `PrepareAsync`, `BeforeDrainAsync`, `AfterDrainAsync` and `AfterFinalDrainAsync`, and Task 6 shrinks it. `kcap watch` becomes argument parsing, process concerns and one recorder. `LineStamp` joins `src/Capacitor.Cli.Core/Models.cs` here because `IRowStamper` names it, and it reaches the wire in Task 11.

- [ ] **Tests:** the existing `WatchCommandTests`, `LiveWatchHardeningAcceptanceTests` and `CursorTailingWatcherTests` pass against the new shape. Add `A_recorder_runs_against_a_fake_transport`, asserting batches, line numbers and the drain-complete call.
- [ ] **Implement:** move the loop. `WatchState` is already in `Capacitor.Cli.Core` and stays there. Vendor code stays reachable through an interface for now, so this task can land without Task 6. Process concerns stay in `WatchCommand`: parent-pid watch, pid file, signals, heartbeat.
- [ ] **Verify:** the unit and integration suites.
- [ ] **Commit:** `Extract the transcript recorder from kcap watch`

### Task 6: Move vendor recording code down

**Files:**
- Move: from `src/Capacitor.Cli/Harness/<Vendor>/` to `src/Capacitor.Cli.Recording/Harness/<Vendor>/`, only what the recorder calls: correlators, ledgers, subagent discovery, title stores. Move `src/Capacitor.Cli/PrDetection/` and `src/Capacitor.Cli/Harness/Titles/`.
- Modify: registration sites per assembly

**Interfaces:**
- Consumes: Task 5's seam.
- Produces: `RecorderHousekeeping : IRecorderHousekeeping` in the recording assembly, holding repository and pull request detection, harness titles and subagent scans, so a daemon can use the same code. What only a host can do goes through one interface, `IRecorderHost`, in the recording assembly, with `WatchRecorderHost` in `src/Capacitor.Cli/Commands/` as `kcap`'s implementation: arming its process watches on connect, stopping the run synchronously, handing a title off, starting and stopping a child source and finishing a Grok child, opening a SQLite-backed title store, reading Antigravity usage, and the two commit coverage probes. A host that can't do one returns null or false. `WatchCommand` stops implementing `IRecorderHousekeeping`, and its per-run `null!` fields go.
- SQLite stays out of this assembly: `AntigravityGenMetadataDb`, `AntigravitySummaryTitle` and `CursorComposerTitle` stay in `kcap`, because the AOT daemon references this assembly and the SQLite native bundle must never reach it. `GitHook`, `AgentSessions`, `GrokSubagentTeardown` and `ImportHarnessTitle` stay in `kcap` too.

- [ ] **Tests:** moved tests pass. Add `The_recording_assembly_does_not_reference_kcap` as an assembly-reference assertion.
- [ ] **Implement:** one vendor per commit. Import sources and hook teardown stay in `Capacitor.Cli` unless the recorder calls them. Namespaces follow directories. Behaviour doesn't change: `KcapWatchHubTests` keeps passing.
- [ ] **Verify:** the suites and the AOT publish check.
- [ ] **Commit, per vendor:** `Move <Vendor> recording code into the recording assembly`

### Task 7: Run many sources in one process

**Files:**
- Create: `src/Capacitor.Cli.Recording/RecorderHost.cs`
- Test: `test/Capacitor.Cli.Recording.Tests.Unit/RecorderHostTests.cs`

**Interfaces:**
- Consumes: `TranscriptRecorder`.
- Produces: `RecorderHost(IRecorderTransport transport, Func<RecordingSource, IRowStamper> stampers, Func<RecordingSource, RecorderContext> contexts, TimeProvider time)` with `bool TryAdd(RecordingSource source)`, `Task FinishAsync(string key, CancellationToken ct)`, `IReadOnlyCollection<string> ActiveKeys`.

- [ ] **Tests:**
  - `A_redaction_hold_stalls_only_its_own_source`: one source held, the other's lines still arrive.
  - `A_throwing_source_does_not_stop_the_others`.
  - `Finishing_a_source_leaves_the_others_running`: finish the child key, and the parent stays in `ActiveKeys`.
  - `Adding_a_key_twice_is_refused`.
  - `Fifty_idle_sources_read_a_bounded_amount_on_a_tick`: under 8 KiB per source per tick, and above a lower bound, because the index's tail compare is what catches a rewrite, so an idle tick isn't zero. It includes a root source past its threshold.
  - `A_failed_source_is_recorded_again_after_a_backoff`: it stays in `ActiveKeys` and `TryAdd` stays refused meanwhile.
  - `A_source_that_ends_cleanly_is_not_retried`.
  - `Finishing_a_source_that_never_connected_still_drains_it`.
  - `Finishing_during_a_backoff_drains_once`.
  - `A_source_that_cannot_connect_at_finish_is_marked_for_import`.
  - `A_connect_that_never_returns_at_finish_is_marked_for_import`.
  - `Backoff_doubles_to_its_cap_and_resets_after_a_long_run`: delays of 1, 2, 4, 8, 16, 30, 30.
  - `Cancelling_a_finish_ends_the_wait_and_not_the_drain`.
- [ ] **Implement:** one loop per source, with failures isolated per source and no shared mutable state beyond the transport. A watcher has no retry, because an exception kills it and the next hook respawns it. No hook does that for a source the daemon holds, so the host retries: it keeps the entry, builds a fresh recorder, context and stamper for each attempt, and waits 1 second doubling to 30 seconds on `TimeProvider`, resetting once an attempt has connected and run past the cap. A clean return from `RunAsync` finishes the source and never retries. The retry has no ceiling here, because the daemon finishes a hosted source when its agent exits. A source that didn't connect is finished by one fresh recorder through `TranscriptRecorder.DrainOnceAsync(CancellationToken ct)`, which prepares, connects and makes the final drain without entering the loop. `RunAsync` keeps its early exit on a stop before connecting, so `kcap watch` doesn't change. That connect is bounded to 30 seconds on `TimeProvider`, because a transport can retry forever and the mark must still be reached. If it fails or times out, the source is marked for import, never dropped silently, and nothing is cut short, because nothing has been read yet. `FinishAsync`'s `ct` bounds the caller's wait only: it never cuts a final drain short, because a drain cut short loses its tail with no spool and no mark.
- [ ] **Verify:** the class passes.
- [ ] **Commit:** `Run many transcript recorders in one process`

---

## Milestone 4: hosted recording and stamps

### Task 8: One recorder per source

**Files:**
- Create: `src/Capacitor.Cli.Core/RecorderClaims.cs`
- Modify: `src/Capacitor.Cli/WatcherManager.cs` (`EnsureWatcherRunning` :309, `KillWatcher` :77, `InlineDrainAsync` :493), `src/Capacitor.Cli.Recording/RecorderHost.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/RecorderClaimsTests.cs`, `test/Capacitor.Cli.Tests.Unit/WatcherManagerClaimTests.cs`

**Interfaces:**
- Produces: `RecorderClaims(string directory, TimeProvider time)` with `RecorderClaimState Read(string key)`, `bool TryTake(string key, string daemonName, bool isHosted)`, `void Beat(string key)`, `void Release(string key)`, `bool IsHosted(string key)`, and `public static readonly TimeSpan Grace = TimeSpan.FromSeconds(60)`, longer than a normal daemon restart.
- A claim is one file per key, beside the watcher pid files and never one of them. Watchers keep their pid files. No claim plus a live pid means a watcher holds the source.

- [ ] **Tests, one for each row of the spec's table:**
  - `Watcher_held_source_stops_and_drains_as_today`.
  - `A_fresh_daemon_claim_signals_nothing_and_asks_the_daemon_to_finish`: assert no signal is sent, `FinishSourceAsync` is called once, and no `/hooks/transcript` post happens.
  - `A_stale_claim_on_a_terminal_session_is_taken_and_drained`.
  - `A_stale_claim_on_a_hosted_session_does_nothing`: no post and no claim change.
  - `An_unclaimed_source_drains_as_today`.
  - `The_claim_never_touches_the_pid_file`: assert `KillWatcher` can't reach the daemon's pid.
  - `EnsureWatcherRunning_spawns_nothing_for_a_fresh_claim`.
  - `A_recorder_that_lost_its_claim_stops_sending` (Review Focus 2): a recorder holding a claim taken by a hook stops at its next tick.
- [ ] **Implement:** claim-awareness lives inside `WatcherManager`, so no vendor call site changes. A source in `RecorderHost`'s backoff keeps beating its claim, so a hosted source stays owned. Task 15 decides this for terminal sources, where not beating would let a hook take a source the daemon can't reach. Add `IDaemonRecordingClient` with `Task<bool> FinishSourceAsync(string key, CancellationToken ct)`; until Task 15 it answers false. `TranscriptRecorder` takes an optional trailing `RecorderClaims? claims = null` and checks its claim each tick when given one.
- [ ] **Verify:** both classes, plus the Claude, Gemini, Codex, Grok, OpenCode, Pi and Copilot hook suites.
- [ ] **Commit:** `Make watcher stop and drain paths honour recorder claims`

### Task 9: The daemon records hosted Claude

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/DaemonRecorder.cs`, `src/Capacitor.Cli.Daemon/Services/ServerRecorderTransport.cs`
- Modify: `src/Capacitor.Cli.Daemon/Capacitor.Cli.Daemon.csproj`, `Services/AgentOrchestrator.cs`, `Services/ServerConnection.cs`, `Services/LocalControlCapabilities.cs`, `src/Capacitor.Cli/WatcherManager.cs`
- Server: `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs` (the `StopWatcher` pushes at :366 and :462), `src/Capacitor.Api.Public/WatcherTracker.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DaemonRecorderTests.cs`; server `test/Capacitor.Api.Public.Tests`

**Interfaces:**
- Consumes: `RecorderHost`, `RecorderClaims`.
- Produces: capability `recording/1`. `StopWatcher(reason)` keeps its one argument, because a SignalR client drops an invocation whose argument count doesn't match its handler. The server pushes `StopSource(string key, string reason)` beside it, which an older watcher has no handler for and ignores. `DaemonRecorder.StartForAgent(HostedAgent agent)` and `FinishAsync(string key, CancellationToken ct)`.

- [ ] **Tests:**
  - `The_claim_exists_before_the_agent_process_starts` (Review Focus 4).
  - `A_hosted_hook_spawns_no_watcher_on_a_daemon_with_recording`.
  - `A_hosted_hook_spawns_a_watcher_on_an_older_daemon`.
  - `A_hard_killed_daemon_resumes_from_WatcherConnect`: resume from the persisted list, with no line lost and none resent past the server's position.
  - `A_restart_skips_a_session_a_live_watcher_holds`.
  - `Stop_with_a_key_stops_only_that_source`, on the server: two keys on one connection.
- [ ] **Implement:** behind a daemon config flag, off by default until Task 12 proves it. Persist the list of recorded sources in the daemon's state directory. Hosted Claude only. The daemon implements `IRecorderHost`. Move `GitHook`'s probing down here, taking `isClaimed` and the hook command from the host: a hosted session is claimed by definition, and the command comes from `KcapBinaryCommand.ResolveCliSibling()`, because `Resolve()` inside the daemon names the daemon binary and would read every hosted session as uncovered. All recorders share one `GitProviderRouter`, which is one per process by design. Route the `Console.Error` writes in `CursorMarkers.Quarantine` and `TranscriptSpool.MarkNeedsImport` to the recorder's log. Decide what the recorder's idle-window environment read means in a daemon, where it reads the daemon's environment rather than the session's. A clean close from the server ends a watcher rather than reconnecting it, so `ServerRecorderTransport` must not inherit that: a daemon's reconnect is the connection's own.
- [ ] **What differs in a daemon,** found while moving the code. Each needs an answer here:
  - `RunWatch` calls `ClaudeJudgeWarmup` and `HostedAgent.FromEnvironment`, so a daemon recorder doesn't get them for free.
  - `git` and `gh` inherit the daemon's environment and PATH, and a failed start reads as "no repository", silently.
  - `GitProviderRouter`'s memo has no expiry, and a failed probe stores GitLab. That costs a watcher one session and would cost a daemon its whole lifetime, so give it an expiry.
  - Child start and title hand-off spawn `Environment.ProcessPath`, which in a daemon is the daemon binary.
  - `RecorderHost` disposes its recorders, so `DaemonRecorder` can't read `EndedIdle`, `SpawnedChildKeys` or a failure, and `RetireAckCursor` is never called. The host needs an on-ended callback, fired per attempt, or a failed attempt's child keys are lost.
  - A server stop during a backoff has no subscriber. No lines are lost, because the next `WatcherConnect` redelivers it, but the server waits only 10 seconds for a drain against a backoff of up to 30, so the session end can overtake the tail, and a substantive line then reactivates an ended session (`CapacitorHub.cs:375`). Settle the order.
  - `ServerRecorderTransport` raises `Reconnected` to every recorder. Raise them with one handler's failure or hang isolated from the rest, because in turn, one stuck `WatcherConnect` stalls every later source's re-registration and N sources cost N round trips.
  - A send that hangs in a final drain has nothing to end it. A daemon shutdown needs a bound on `RecorderHost`, and it must spool rather than cut a drain short.
  - `ServerRecorderTransport.ConnectAsync` must honour its token. A running connect that ignores it blocks both the finish and the import mark, because only a drain-once connect has a bound.
  - A `WatcherConnect` that completes after its attempt was abandoned leaves a registration on the server until the connection drops, which on a daemon's connection is a long time. `StopAndDrainAsync` then waits out its 10 seconds for a source nobody is recording.
  - An `IOException` in both the final read and the tail read in `TranscriptRecorder` returns normally with lines neither sent, spooled nor marked, and the host's mark isn't reached. `kcap watch` shares this. Close it before the daemon relies on `FinishAsync`, and log `MarkNeedsImport`'s false in `TranscriptRecorder` as `RecorderHost` does.
  - `RecorderHost` guards a context's `Log` and `Beat`, but `RecorderHousekeeping` takes its own log delegate, so guard that one too.
- [ ] **Verify:** the suites, then one real hosted Claude session on a VM, comparing its server transcript to today's watcher path.
- [ ] **Commit:** `Record hosted Claude sessions in the daemon`

### Task 10: Stamp proof (gate G2)

The spec opens Part 2 with this measurement. It's a throwaway branch on top of Task 9.

- [ ] **Build:** log every submission and every person-authored row with timestamps on real hosted Claude and Codex sessions.
- [ ] **Run, and record:** one row per pasted message, in order, across identical text from two senders, a mid-turn paste, delayed transcript writes, slash commands and skills, attachment trailers, an Enter answering a dialog, an empty submit, a multiline paste, repeated submit Enters, and a mid-turn paste's row kind. Then run a week of normal use and measure how many sessions would suspend.
- [ ] **Decide G2:**
  - Where order or text doesn't hold, narrow the match rule in the spec and leave those cases on the FIFO matcher.
  - If routine suspension would leave little for stamps to do, stop and bring it back to the owner.
- [ ] **Record:** update the spec. Commit it alone: `Record the stamp proof's results`.

### Task 11: Stamps on the wire and in attribution

**Files:**
- Server: `src/Capacitor.Api.Public.Abstractions/Sessions/TranscriptBatch.cs`, new `LineStamp.cs`, `src/Capacitor.Server.Services/ReadModels/HostedAgentInputProjector.cs`, `src/Capacitor.Server.Core/Chat/UserMessageClassifier.cs` only if the proof needs it, and the transcript append path
- CLI: `src/Capacitor.Cli.Core/Models.cs`
- Test: `test/Capacitor.Server.Tests.Agents`, `test/Capacitor.Server.Tests.Ingest`

**Interfaces:**
- Produces: `TranscriptBatch.Stamps : LineStamp[]?`. The event metadata keys `dispatch_id` and `local_input`.

- [ ] **Tests:**
  - `A_stamped_row_takes_its_dispatch_without_matching_text`, with redacted text that would fail FIFO.
  - `An_unstamped_row_still_goes_to_the_matcher`.
  - `A_local_row_goes_to_the_owner_and_consumes_no_pending_dispatch`: identical text, remote dispatch pending, local row first, then the stamped row links to its dispatch only.
  - `A_replayed_stamped_row_does_not_attribute_twice`.
  - `An_older_batch_without_stamps_deserializes_unchanged`.
- [ ] **Implement:** the projector checks metadata first. A row with `local_input` skips FIFO entirely.
- [ ] **Verify:** those projects, then raise the `src/cli` submodule in a follow-up once the CLI change lands.
- [ ] **Commit:** `Attribute stamped transcript rows without matching text`

### Task 12: Input log and matching

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Services/InputLog.cs`, `InputLogStamper.cs`
- Create: `src/Capacitor.Models.Transcripts/Harness/Claude/ClaudePersonRows.cs`, `.../Harness/Codex/CodexPersonRows.cs`
- Modify: `Services/AgentOrchestrator.cs` (`DeliverInputAsync`, `HandleSendSpecialKey` :5376), `Services/AgentOrchestrator.LocalIpc.cs` (Stdin at :824, `AnswerSendTextCoreAsync`), `src/Capacitor.App/ViewModels/TerminalChatInput.cs:69`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/InputLogTests.cs`, `InputLogStamperTests.cs`; `test/Capacitor.App.Tests.Unit`

**Interfaces:**
- Produces: `InputLog(string path, TimeProvider time)` with `long AppendPending(Guid? dispatchId, string sender, string text)`, `void MarkWritten(long position)`, `void Suspend(string reason)`, `bool IsSuspended`, `LineStamp? Match(int lineNumber, string rawLine, string? personText)`, `void Acknowledge(int throughLine)`, `void ResetForNewProcess()`.
- `IPersonRows.PersonText(string rawLine) : string?`, in `src/Capacitor.Models.Transcripts/IPersonRows.cs`, is null for anything a person didn't author.
- Text is stored as a SHA-256 hash of the text with trailing newlines trimmed.

- [ ] **Tests:**
  - `Identical_texts_from_two_senders_are_told_apart_by_position`.
  - `A_row_with_no_matching_entry_goes_unstamped_and_suspends`.
  - `A_raw_chunk_with_a_newline_suspends`, and `A_special_key_Enter_suspends`.
  - `A_raw_chunk_without_a_newline_does_not_suspend`.
  - `A_raw_Enter_never_makes_an_entry`.
  - `Idle_and_end_of_file_do_not_resume_stamping`.
  - `A_new_process_resumes_stamping_from_the_first_line_after_it`.
  - `Suspension_survives_a_daemon_restart`.
  - `A_Pending_entry_at_recovery_suspends`.
  - `A_replayed_row_takes_its_stamp_from_the_match_record_and_consumes_no_entry`: identical A and B, crash after matching U to A and before the ack, and U must carry A on replay.
  - `A_match_record_goes_only_after_the_line_is_acknowledged`, including spooled lines and the final drain.
  - `The_log_holds_no_message_text`: scan the file for the plaintext.
  - `A_local_message_marks_its_row_local`.
  - `TerminalChatInput_sends_plain_text_through_SendText`, in the app tests.
- [ ] **Implement:** append and flush before the PTY write, mark Written after it. The runtime's own repeated submit Enters belong to the entry and don't suspend. `InputLogStamper : IRowStamper` wires the log to the recorder. Honour whatever G2 narrowed.
- [ ] **Verify:** the suites. Then turn Task 9's flag on for hosted Claude on one VM and compare stamped attribution to FIFO on the same sessions for a week.
- [ ] **Commit:** `Stamp hosted rows from a persisted input log`, then `Send composer text through SendText from the terminal tab`

### Task 13: Launch prompts carry their dispatch

**Files:**
- Server: `src/Capacitor.Api.Public.Abstractions/Agents/Commands/LaunchAgentCommand.cs`, `src/Capacitor.Server.Services/Agents/HostedAgentInputQueue.cs:101`, `src/Capacitor.Api.Public/Agents/DaemonCommandExtensions.cs`
- CLI: the daemon's `LaunchAgentCommand` binding (`Services/ServerConnection.cs:345`), `Services/AgentOrchestrator.cs` launch path
- Test: `test/Capacitor.Server.Tests.Agents`, `test/Capacitor.Cli.Daemon.Tests.Unit`

**Interfaces:**
- Produces: an optional trailing `Guid? LaunchDispatchId` on `LaunchAgentCommand`.

- [ ] **Tests:** `A_launch_prompt_row_is_stamped_with_its_minted_dispatch`, and `An_older_daemon_ignores_the_launch_dispatch`.
- [ ] **Implement:** pass the id from `RegisterLaunchPrompt` through to the launch's `AppendPending`. The paste follows the existing launch path, so a launch prompt never reaches the command line.
- [ ] **Verify:** those suites.
- [ ] **Commit:** `Carry a launch prompt's dispatch id to the daemon`

### Task 14: Hosted Codex PTY

**Files:**
- Modify: `DaemonRecorder.cs` vendor gate, `CodexPersonRows.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/DaemonRecorderTests.cs`, `test/Capacitor.Models.Transcripts.Tests.Unit/Harness/Codex/`

**Interfaces:**
- Consumes: Tasks 9, 11 and 12.

- [ ] **Tests:** `Codex_rollout_user_rows_map_to_person_text`, using the row kinds Task 10 recorded, and `A_Codex_redacted_line_still_stamps_by_its_raw_hash`.
- [ ] **Implement:** add Codex PTY to the gate. App-server Codex stays as it is.
- [ ] **Verify:** one real hosted Codex session on a VM.
- [ ] **Commit:** `Record hosted Codex PTY sessions in the daemon`

---

## Milestone 5: terminal and other-vendor recording

### Task 15: Hooks hand terminal sessions to the daemon

**Files:**
- Modify: `src/Capacitor.Cli.Core/LocalIpc/FrameType.cs`, `LocalControlOps.cs`, `src/Capacitor.Cli.Daemon/Services/LocalControlServer.cs`, `LocalControlCapabilities.cs`, `src/Capacitor.Cli/WatcherManager.cs`
- Test: `test/Capacitor.Cli.Tests.Unit/WatcherManagerHandoffTests.cs`, `test/Capacitor.Cli.Daemon.Tests.Unit/Services/LocalControlRecordingTests.cs`

**Interfaces:**
- Produces: frames `RecordSource` and `FinishSource` and their acks, at the next free values. `IDaemonRecordingClient.TryRecordAsync(RecordingSource source, CancellationToken ct) : Task<bool>`, and a real `FinishSourceAsync`.

- [ ] **Tests:**
  - `A_hook_hands_a_terminal_session_to_its_daemon`.
  - `A_hook_on_another_profile_spawns_a_watcher` (Review Focus 3).
  - `No_daemon_spawns_a_watcher_as_today`.
  - `An_older_daemon_spawns_a_watcher`.
  - `A_daemon_restart_within_grace_spawns_no_watcher`.
  - `A_daemon_gone_past_grace_spawns_a_watcher_and_keeps_it_until_the_session_ends`.
  - `A_failed_handoff_never_blocks_the_hook_past_its_budget`.
- [ ] **Implement:** match the daemon by the hook's resolved profile and server URL. `EnsureWatcherRunning` tries the daemon first, under the per-key spawn lock.
- [ ] **Verify:** the suites, then a real terminal session with the daemon killed and restarted mid-turn.
- [ ] **Commit:** `Hand terminal sessions to the daemon for recording`

### Task 16: Every vendor, in order

**Files:**
- Modify: the vendor gate in `DaemonRecorder.cs`, one vendor per PR
- Test: per-vendor recorder tests

- [ ] **Tests:** for each vendor, `<Vendor>_session_end_finishes_through_the_daemon`, and for vendors with subagents `<Vendor>_subagent_stop_leaves_the_parent_running`.
- [ ] **Implement:** terminal Claude first, then the others. Keep each behind the gate until a real session matches its watcher output. Cursor and Antigravity read SQLite, which must never reach the AOT daemon, so their title stores and Antigravity's usage rows have no daemon reader. Those two stay on the fallback watcher until there's a reader the daemon can carry.
- [ ] **Before a vendor with subagents moves:** a source that never had a tick gets no `AfterDrainAsync` subagent scan, which matters for Codex, Gemini and OpenCode. A host can't decline by returning false yet. The Gemini, Mistral Vibe, OpenCode, Codex and Grok scans ignore `StartChildAsync`'s result, so false still logs "spawned" and, except for Grok, marks the file seen. False from `FinishGrokChildAsync` means retry, every tick, forever. Copilot: a null parent pid sets `CopilotHostMissing`, which changes idle end, and a daemon has no parent pid to pass. Muse: `MuseHookCommand` decides by pid-file liveness, which a daemon recorder doesn't have, and `KCAP_WATCHER_DIR` must match the hook's.
- [ ] **Verify:** one real session per vendor on the MacVisor VM.
- [ ] **Commit, per vendor:** `Record <Vendor> terminal sessions in the daemon`

---

## Milestone 6: steering (needs G1)

### Task 17: Server dispatch states

**Files:**
- Modify: `src/Capacitor.Server.Services/Agents/HostedAgentInputQueue.cs`, `HostedAgentInputDispatcher.cs`, `DaemonConnectionLifecycle.cs`, `src/Capacitor.Api.Public/Sessions/CapacitorHub.cs`, `src/Capacitor.Server.Services/ReadModels/HostedAgentInputProjector.cs`, `src/Capacitor.Server.Services/Sessions/PendingDispatchFlusher.cs`, `src/Capacitor.Server.Services/Persistence/CapacitorSchema.cs`, `CapacitorDb.cs`
- Create: events `HostedAgentInputHandedOver`, `HostedAgentInputLinked`, `HostedAgentInputUnconfirmed`
- Test: `test/Capacitor.Server.Tests.Agents/AttachedInputStatesTests.cs`

**Interfaces:**
- Produces, as hub methods: `Task<bool> ConfirmInputHandover(Guid dispatchId, string agentId)`, `Task ReportInputLink(Guid dispatchId, string agentId, string sessionId, Guid rowUuid)`, `Task ReportInputUnconfirmed(Guid dispatchId, string agentId, string reason)`. Rejection uses the existing `SendInputRejected`.
- Applies only to agents with `RuntimeTransport == "mod"`, from a constant this task adds and Task 18 registers with. Hosted agents are untouched.
- Rejection reasons, as constants beside the transport value: `"stopped"` and `"command"`. Task 21 turns them into copy.

- [ ] **Tests, one for each row of the spec's transitions table:**
  - `A_handover_is_confirmed_for_a_held_dispatch`.
  - `A_handover_is_refused_for_a_rejected_dispatch`.
  - `A_dropped_connection_rejects_held_dispatches`, and it needs no daemon report.
  - `A_dropped_connection_leaves_dispatches_with_the_mod`.
  - `A_link_delivers_a_dispatch`.
  - `A_drop_rejects_a_dispatch_with_the_mod`.
  - `Nothing_else_rejects_a_dispatch_with_the_mod`.
  - `A_daemon_away_past_grace_moves_with_the_mod_to_unconfirmed`.
  - `A_late_link_moves_unconfirmed_to_delivered`.
  - `No_handover_is_confirmed_while_one_is_outstanding`.
  - `An_unconfirmed_dispatch_rejects_new_messages_with_that_reason`.
  - `A_link_before_the_row_and_a_row_before_the_link_both_attribute`.
  - `A_link_after_a_daemon_restart_is_accepted`.
  - `The_matcher_skips_a_dispatch_held_for_the_mod`: identical typed text arrives first and takes the owner fallback, and the dispatch stays pending.
  - `A_flush_at_session_end_skips_with_the_mod_and_unconfirmed`: no undelivered message is appended for a dispatch that might still land.
  - `An_unconfirmed_row_is_cleared_when_the_process_exits`, and the dispatch stays Unconfirmed on its stream.
  - `A_link_is_found_by_dispatch_id_alone`: the link names a session the dispatch was never sent to, and arrives before the replacement agent attaches.
  - `A_row_in_a_later_session_attributes_in_both_orders`.
  - `A_message_starting_with_a_slash_is_refused_before_reservation`: leading spaces and newlines count, and a hosted agent still takes it.
  - `A_stopped_rejection_keeps_its_reason`.
- [ ] **Implement:** add no table. The stream is the source of truth: `ConfirmInputHandover` and the barrier decide by reading `HostedAgentInput-{agentId}` and appending at an expected version, so a handover can't race a rejection through projection lag. Add nullable `delivery_state` and `linked_row_uuid` to `hosted_agent_pending_dispatches`, with no rebuild. A non-null `delivery_state` keeps a row away from the FIFO matcher and from `PendingDispatchFlusher`, and hosted dispatches leave both columns null. Claude's first event id for a row is the row's uuid, so a link arriving first sets `linked_row_uuid`, and the row then writes the `dispatched` attribution and deletes the pending row. A row arriving first takes the owner fallback, and the link then updates that attribution row by `event_id` to `dispatched` with its dispatch id. The sender is the owner in both orders. Match a row to `linked_row_uuid` by uuid alone, because the row can land in a later session.
- [ ] **Verify:** the class, plus the existing `HostedAgentInputProjector` and `PendingDispatchFlusher` tests.
- [ ] **Commit:** `Track delivery states for attached agents`

### Task 18: Attached agents on the server

**Files:**
- Modify: `src/Capacitor.Api.Public.Abstractions/Agents/Daemon/AgentRegisteredArgs.cs`, `src/Capacitor.Server.Services/Agents/AgentInstanceRegistry.cs`, `HostedAgentAccessPolicy.cs:165`, `DaemonConnectionLifecycle.cs:47`, `src/Capacitor.Api.Public/Permissions/PersistedInterruptResolver.cs`
- Test: `test/Capacitor.Server.Tests.Agents/AttachedAgentTests.cs`

**Interfaces:**
- Produces: `RuntimeTransport` value `"mod"`, and trailing `string? ReplacesAgentId` and `string? ProcessKey` on `AgentRegisteredArgs`. Attached agent ids are derived from the session id, so they're stable across daemon restarts. The process key tells a second process from the same one attaching again.

- [ ] **Tests:**
  - `An_attached_agent_takes_input_from_its_owner_only`: a teammate with Full, an input right and a grant is refused.
  - `Hosted_agents_keep_their_rules`.
  - `A_reported_attached_agent_survives_a_daemon_reconnect`.
  - `An_attached_agent_is_never_reachable_for_permission_answers`.
  - `A_replacement_agent_inherits_the_barrier`.
  - `A_replacement_with_nothing_outstanding_takes_input_at_once`.
  - `A_second_process_on_one_session_is_refused` (Review Focus 5).
  - `The_same_process_attaching_again_is_accepted`: after a module reload and after a daemon restart.
  - `A_second_process_attaches_once_the_first_has_lapsed`.
- [ ] **Implement:** the expected-transport check accepts `"mod"` for a registration with no launch, which is why it sits behind the daemon's `steering/1` capability.
- [ ] **Verify:** the class.
- [ ] **Commit:** `Register attached terminal agents with owner-only input`

### Task 19: Daemon side

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Harness/Claude/ModAttachedAgentRuntime.cs`, `src/Capacitor.Cli.Daemon/Services/SteeringEndpoint.cs`
- Modify: `Services/LaunchConsentStore.cs` (`PolicyDoc`), `Services/AgentOrchestrator.cs`, `RestartCoordinator.cs`, `LocalControlServer.cs`, `LocalControlCapabilities.cs`, `src/Capacitor.Cli.Core/DaemonStore.cs`
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Services/SteeringEndpointTests.cs`, `Harness/Claude/ModAttachedAgentRuntimeTests.cs`, `Services/LaunchConsentStoreTests.cs`

**Interfaces:**
- Produces: routes on a new owner-only socket, `DaemonStore.SteeringSocketPath(name)`: `POST /attach`, which carries the process key, `POST /next`, `POST /link`, `POST /undelivered`, `POST /detach`. Capability `steering/1`, not advertised on Windows. A `steering` field on the consent document, `"on"` or `"off"`. A CLI command `kcap daemon steering-socket` that prints the socket for the current profile, or nothing when off, and `kcap daemon steering on|off`.

- [ ] **Tests:**
  - `Stop_touches_nothing_in_the_process`: no signal is sent to the attached pid.
  - `The_runtime_offers_no_terminal_keys_or_resize`.
  - `An_attached_agent_does_not_hold_up_a_restart`.
  - `Next_returns_nothing_until_the_server_confirms`.
  - `Held_dispatches_are_dropped_when_the_connection_drops`.
  - `The_socket_is_owner_only`.
  - `Attachments_survive_a_restart_and_are_reported_on_reconnect`.
  - `A_lapsed_attachment_marks_the_outstanding_dispatch_unconfirmed`.
  - `A_missing_or_corrupt_switch_reads_as_off`.
  - `A_switch_off_daemon_refuses_attach`.
  - `A_session_on_a_fallback_watcher_can_still_attach`.
  - `A_malformed_request_is_refused_without_harming_the_daemon`.
  - `Attach_passes_the_process_key_to_the_server`.
  - `Steering_is_refused_on_windows`: `kcap daemon steering on` says why, and the capability isn't advertised.
- [ ] **Implement:** a minimal HTTP/1.1 reader over `UnixDomainSocketEndPoint`, because `HttpListener` can't bind a Unix socket. Keep it AOT-clean. No token: the socket's mode is the authentication.
- [ ] **Verify:** the suites and the AOT publish check.
- [ ] **Commit:** `Add the daemon's steering endpoint and attached runtime`

### Task 20: The mod

**Files:**
- Create: `kcap/steering/.claude-plugin/plugin.json`, `kcap/steering/hooks/hooks.json`, `kcap/steering/hooks/register.ts`, `kcap/steering/hooks/register.test.ts`
- Modify: `kcap/.claude-plugin/marketplace.json`

**Interfaces:**
- Consumes: Task 19's routes, `$.process` for `kcap daemon steering-socket`, `$.http.fetch(url, { socketPath })`.
- Produces: plugin `kcap-steering`.

- [ ] **Tests (`claude plugin test`):**
  - `It_stays_inert_with_no_socket`.
  - `It_submits_one_dispatch_at_a_time_from_a_timer`.
  - `It_asks_for_a_dispatch_only_when_no_turn_is_running`, including after a reload mid-turn.
  - `It_links_a_row_only_when_its_text_matches`.
  - `A_row_with_nothing_outstanding_is_reported_unmatched`.
  - `A_drop_reports_rejected`.
  - `A_refused_submit_reports_rejected`.
  - `A_drop_after_an_aborted_turn_reports_stopped`, and input since the abort clears it.
  - `Idle_with_no_row_after_grace_reports_undelivered_and_never_resubmits`.
  - `The_outstanding_dispatch_survives_a_reload_and_a_session_change`.
  - `Entries_are_keyed_by_process_and_stale_keys_are_removed`.
  - `Session_end_on_clear_attaches_a_replacement_naming_the_old_agent`, with the new id taken from `classic.SessionStart`.
  - `A_refused_attach_retries_until_the_first_process_has_gone`.
  - `An_unreported_link_is_retried_after_the_daemon_returns`.
  - `A_remote_message_shows_a_notice_at_the_terminal`.
  - `A_failing_hook_passes_the_row_through`.
- [ ] **Implement:** every hook has a `.catch` that passes through. Nothing in this plugin can reach recording. Keep the outstanding dispatch, any unreported link and the running-turn flag in `$.store`, because a module variable dies on reload and `$.state` dies on `/clear` and resume. The process key is the pid plus its start time: `$.process.run(['sh','-c','echo $PPID'])` gives Claude's pid, since `$.env.get('CLAUDE_PID')` is unset in a module, and `ps -o lstart= -p <pid>` the start time. Track a running turn from `turn.start` and `turn.complete`, and don't parse the engine's reason text. Helpers are closures in `session.start`, because a mod can't bind or pass `$`. Grace is 10 seconds: a late link corrects a false alarm, so it only has to outlast a normal landing.
- [ ] **Verify:** `claude plugin validate kcap/steering`, `claude plugin test kcap/steering`, and `tsc -p kcap/steering`.
- [ ] **Commit:** `Add the kcap-steering mod`

### Task 21: Setup and surfaces

**Files:**
- Modify: `src/Capacitor.Cli/Commands/SetupCommand.cs`, `src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginInstaller.cs`, `README.md`, help text in `src/Capacitor.Cli.Core/Resources/`
- Modify: `src/Capacitor.App` agent rail and chat states
- Server: the web and mobile surfaces that read `HostedAgentInputRejected`

**Interfaces:**
- Consumes: `kcap daemon steering on|off`, Task 17's states.

- [ ] **Tests:**
  - `Setup_offers_steering_and_leaves_it_off_by_default`.
  - `Turning_steering_on_installs_and_enables_the_plugin_once`.
  - `Turning_steering_off_leaves_the_plugin_installed`.
  - `An_attached_agent_does_not_read_as_hosted_in_the_rail`.
  - `Each_state_shows_its_copy`, with the spec's exact strings: "Waiting for the turn to end", "Delivered", "Not delivered, send again", "Delivery unknown, check the session before sending again", "Not delivered, the session was stopped at the terminal", "Not delivered, commands can only be run at the terminal".
  - `Setup_does_not_offer_steering_on_windows`.
  - `A_session_waiting_for_approval_shows_that_it_is_waiting_at_the_terminal`.
- [ ] **Implement:** the install is a settings write through `ClaudePluginWriter`, once per machine. Stop on an attached agent reads as disconnecting steering, and never as stopping the agent.
- [ ] **Verify:** the suites, then one end-to-end run on a macOS VM and one on Linux, each with kcap's own plugin and hooks loaded, which the proof didn't have: start `claude` in a terminal, send from the web, and watch the row land with the sender's name. If `socketPath` fails on Linux, Linux waits for its own transport as Windows does.
- [ ] **Commit:** `Offer steering in setup and show delivery states`

---

## Outside this plan

- **Remote approvals and interrupt.** The owner wants these straight after Milestone 6. They need their own short plan, once Milestone 6 has shown how the attached agent behaves.
- **Server-side message queue.** It needs its own design.
- **Retiring what's left of the FIFO matcher.** It depends on an exact signal for raw typing, which doesn't exist.

## Gates

| Gate | After | If it fails |
|---|---|---|
| G1, steering proof | Task 3 | Passed on macOS on 2026-11-06. The spec records the results. Windows and Linux `socketPath` stay open |
| G2, stamp proof | Task 10 | The match rule narrows, or Tasks 11 to 13 stop and go back to the owner |

Milestones 3 to 5 and Milestone 6 can run side by side. Milestone 6 needs nothing from Milestones 3 to 5.
