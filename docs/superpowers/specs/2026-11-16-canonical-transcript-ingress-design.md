# Canonical transcript ingress

Status: design, awaiting owner approval. Part of kcap #2638 (move transcript normalisation from the server to the client).

Two repositories change: `kcap` (this one) and `kcap-server`. File references to the server are to its `main` at `225d2208f`; references to this repository are to `main` at `800829b8d`.

## Intent

Today a client sends raw transcript lines and the server turns them into canonical events with one normalizer per vendor. `Capacitor.Models.Transcripts` now projects all thirteen vendors to the same events, checked against the server's golden baselines by `LibraryParityTests`.

This design adds the path that uses it: the client projects the lines itself and sends canonical events, and the server validates and appends them without parsing any vendor format. A vendor format change then needs a CLI release only, and the server stops being the place where every vendor's transcript shape is known.

## Decisions the owner made

- Claude Code switches first. The switch is per vendor, and the order of later vendors is decided separately.
- One shared "project and post" path serves the live recorder, the inline drain, `kcap import` and the spool replay, so a vendor switches everywhere at once.
- The server's ingress is vendor-neutral. The capability is one yes or no: "this server takes canonical events".
- Which vendors use the ingress is decided by the CLI release, not negotiated.
- The server can withdraw the capability as a whole, by configuration. That is the only brake: all or nothing, no per-vendor switch on the server.
- The server keeps its normalizers, because older CLIs keep posting raw lines.
- A dispatch stamp, when it comes, is metadata on the canonical user message event. It is not a transcript line.
- The library never writes `default_visibility: "unset"`. The server keeps reading it on stored events, and stamps it itself on a session start it appends from a transcript.

## Success criteria

The Claude switch is done when all four hold:

1. A Claude session recorded through the new ingress shows the same or more in the web app, the desktop app and mobile as one recorded through raw lines, and never less.
2. Re-importing a session the server already holds, recorded through either path, appends nothing twice.
3. The server never ran the Claude normalizer for lines sent through the new ingress.
4. An older CLI that still posts raw lines keeps working unchanged.

Criterion 1 is the owner's wording: the new path produces the same or more than the server does from raw lines, but no less. Three consequences:

- Everything the raw path stores for a session, the new path stores too, with the same ids.
- The additions known today are pinned in `ClaudeServerDeltaTests`: every content block of an assistant record and every tool result of a line become events, where the server reads the first only, and a prompt that was resubmitted unanswered is retracted.
- "More" means more of the session, not different numbers. A difference in usage or cost is a defect to close before the switch. The plan must check the one a review reported: a noise record between two assistant records with identical usage clears `PreviousUsage` in the library but not on the server, which would count that usage twice.

## Non-goals

- Switching any vendor other than Claude. The contract must not block them, and the notes under "Later vendors" say what each will need.
- Deleting server normalizers, or the capture repair comparison that also runs them (`CaptureComparisonBuilder`).
- Producing or storing dispatch stamps. The contract reserves the slot only.
- Changing canonical event shapes, event ids, or the accepted parity exemptions.
- Changing the hosted ACP lane (`AcpSessionEvents`), the Cursor hook backfill or the Muse Assistant poster. They keep their current paths.

## Anti-goals

- A second place that reads a vendor format. Title lines, turn-end lines and question lines are read by the library, next to the projection, not by the recorder and not by the server.
- A client able to set a field the server owns: session owner, visibility, ingester, agent host, origin, delivery state.
- Loss or duplication when a session moves between the two paths in either direction, mid-session included.
- A persistence format for projection state. State is rebuilt from the transcript.

## Current state, in short

Server (`SessionWriter.TranscriptPipeline.cs`, `ProcessTranscriptBatchAsync`): admission and ownership checks, a per-stream gate, a line-number high-water mark, held lines in `unstored_transcript_lines`, `NormalizeLine`, sorting, a seen-id set, per-entry writes with retries, then stamping and side effects. Three side effects read the raw line: harness titles (`IHarnessTitleExtractor`), turn end (`ITranscriptNormalizer.EndsTurn`) and question tracking (`entry.Uuid` / `entry.ParentUuid`). Capture-loss lines (`CaptureLossParser`) are kcap's own format and are parsed before the normalizer.

Client: the recorder (`TranscriptRecorder.DrainAndSendAsync`) reads complete lines, redacts them, and sends a `TranscriptBatch` over the hub (`SendTranscriptBatch2`). Six other producers post a `TranscriptBatch` to `POST /hooks/transcript` on their own. Progress is line-based everywhere: the server reports the line it wants next, and the client resumes there.

`LibraryProjectionNormalizer` in the server's parity tests already runs the library through the production pipeline. It is the reference for what "apply a projection result" means: it applies amendments to events of the same batch, turns `UsageApplied` into stamps and backfills, calls `MarkPersisted` after a batch, and converts a `CanonicalEvent` into the pipeline's `NormalizedEvent`.

## Design

### 1. Wire contract

A new batch type, defined once in `Capacitor.Models.Transcripts` so both repositories compile against the same shape (the server already pins this repository as `src/cli`).

Batch fields:

| Field | Meaning |
|---|---|
| `format` | Integer, `1`, for the envelope only. A server that does not know the value refuses the batch as unsupported. New event types, payload fields or metadata keys do not change it |
| `sessionId`, `agentId`, `vendor` | As in `TranscriptBatch` |
| `lines` | One record per source line this batch covers, in order, including lines that produced no event: `number`, `uuid`, `parent_uuid`, `record_type`, `timestamp`. These are the four fields the server's pipeline reads from a raw line after normalisation |
| `events` | The events, each naming its source line |
| `titles` | Harness title sightings: kind (summary, auto, rename), text, source line, and for a rename the time it took effect |
| `turnEndLine` | Highest covered line that ends a turn, or absent |
| `captureLoss` | kcap's own capture-loss lines, verbatim, with their line numbers |
| `repository`, `observedCommits`, `strict`, `moreFollows` | As in `TranscriptBatch` |

Event fields: `type`, `payload`, `eventId`, `line`, `timestamp`, `recordTimestamp`, `causedBy`, `usage`, `usageIsEcho`, `cacheCreationTokens`, `vendorMetadata`, `attachments` (id, message id, block index, file name, content type, bytes), and a reserved optional `stamp`.

Rules:

- `type` and `payload` are the stored form: the event type name and the JSON the server stores today. `CanonicalEventJson` already parses that form; it gains the matching writer. Every name in `CanonicalEventTypes` must round-trip.
- There is no free-form metadata bag. The server builds stored metadata from the named fields, exactly as `LibraryProjectionNormalizer.Convert` does. `vendorMetadata` keys pass an allow-list.
- All events of one source line travel in one batch. A batch never ends inside a line.
- The reply is today's `TranscriptProgress`, plus a distinct "unsupported" refusal (see 2).
- A Claude `custom-title` line has no timestamp. The server times a rename from the timestamp of the last line before it, including lines that produce no event (`LineTimestamp`, `pendingRenameInferredAt`). The library reader tracks that timestamp and sends the result, because the ingress sees events only.

### 2. Capability and brake

- The server exposes one capability answer, over the hub and over HTTP: "canonical ingress, format 1". A missing method or route means no.
- The server has one configuration switch. Off means the capability answer is no and a canonical batch is refused as unsupported.
- The client asks once per connection (recorder) or once per process (HTTP producers) and does not persist the answer.
- The server refuses a whole batch as unsupported, and stores nothing from it, when it does not know the `format`, an event `type`, a payload field or a `vendorMetadata` key, or when a payload does not parse. It never drops a field or a line it does not understand. The payload parser therefore runs strict, unlike `CanonicalEventJson` today, which ignores unknown fields.
- Any other refusal of a canonical batch that a retry would not change (a 400 or 413 from request binding, for example) also sends that batch raw once: the server rejected the encoding, not the lines. A retryable status (401, 408, 429, 5xx) is handled as it is for a raw batch.
- An "unsupported" refusal of a batch makes that source fall back to raw lines, starting with the refused lines, until it next connects. Raw lines are always accepted, so there is no loop. This is how the brake reaches clients that are already connected, and how a client on a newer event schema than the server degrades.
- Combinations: new CLI with old server, old CLI with new server, and new CLI with the switch off all end on raw lines.

### 3. Client: one shared path

One component takes redacted lines with their line numbers plus the batch context, and decides how they go out:

- Vendor in the release's canonical list (first release: Claude only) and the server said yes: project, assemble, send as a canonical batch.
- Otherwise: send a `TranscriptBatch`, as today.

The live recorder, `WatcherManager.InlineDrainAsync`, `SessionImporter` with its `*ImportSource`s, and `LifecycleSpoolDrain` all send through it. The canonical vendor list is one registry; adding a vendor later is one entry plus whatever that vendor's notes below require.

Assembling a batch is the production form of what `LibraryProjectionNormalizer` does in the server's tests:

- `BeginBatch` before, `MarkPersisted` for every sent event id once the server acknowledged the batch.
- Amendments are applied to their target when the target is in the same batch, and dropped otherwise.
- `UsageApplied` becomes stamps on same-batch targets and a backfill event for targets already acknowledged.
- A rejected line is a covered line with no events, in strict mode too. That is today's behaviour for a known vendor: a line the normalizer throws on is logged and stepped over, and `Failed` counts write failures only.
- Title, turn-end and question signals come from library readers that sit beside the Claude projection and match the server's `ClaudeHarnessTitleExtractor`, `ClaudeCodeNormalizer.EndsTurn` and the question tracker's inputs. Root transcript only for titles; no sidechain turn ends.

Ids are computed from the redacted line and the line number that is sent, as the id contract requires.

### 4. Position, resume and projection state

- Position stays line-based. The server's frontier, `WatcherConnect`, `last-line` and the high-water mark keep their meaning. `lines` advances the frontier even when a line produced nothing.
- Projection state lives in memory only. When a producer starts at a line other than zero, it replays the lines from zero to its start through the projection and the signal readers, discarding the output.
- A source goes canonical only when that replay covered every earlier line. If the transcript is missing or shorter than the start line, a spooled tail carries no transcript path, the replay throws, or it runs out of its budget, the source sends raw lines until it next connects. Projecting a tail without its earlier lines could count a repeated usage as new, which criterion 1 forbids.
- Replay budgets: two seconds for the inline drain and for the recorder's final drain (both run while a session is ending), half the pass for a spool drain, none for import and for the recorder's resume.
- The replay reads raw lines, and makes for each the capture decision the live path made: a line the live path replaced by a capture-loss marker (over the redactor's size limit, or unreadable) is not projected. A large readable line, where only redaction can tell, is decided by running the producer's own redaction on that one line; a timeout there makes the replay incomplete. Replayed state can still differ from the original run for a record with no `uuid`, whose fallback id hashes the redacted line; that is accepted.
- After a failed canonical send the recorder does not encode further lines on the same route. It rebuilds the route by replaying to its cursor, and after a second failure sends raw until it next connects.
- The spool keeps storing `TranscriptBatch` with redacted raw lines. Projection happens when the spool is drained. A tail spooled by an older CLI therefore drains after an upgrade.
- Moving between paths mid-session never duplicates or loses an event: both paths use the same line numbers and the same event ids, so the high-water mark and the seen-id set cover any overlap.
- A move from canonical to raw starts the server's Claude normalizer with empty state (`PreviousUsage`, `ClaudeUnansweredPrompt`, `ClaudeRetractions`), exactly as a server restart does today. The first lines after the move can stamp a usage as new when it is an echo, or miss a retraction. That is accepted; no state is seeded. The same holds for a raw tail that follows a canonical stretch for any reason, including the replay rule above: it is reachable at the end of very large sessions. The server clears that normalizer state whenever a canonical batch moves the stream forward, so a later raw stretch never resumes with state left by an earlier one.

### 5. Server ingress

A new hub method and a new HTTP route accept the canonical batch. Both run the same admission as the raw path: authorization, deleted-session and foreign-session refusal, id safety, vendor check when strict.

The batch then enters `ProcessTranscriptBatchAsync` at the point after `NormalizeLine`. Everything that works on normalized entries is unchanged: sorting, the seen-id set, per-entry writes and retries, the oversize drop, attachments, commits, repository detection, permission and reactivation handling, the user message stamper, the frontier.

What differs:

- No normalizer is resolved or called.
- Titles, turn end and question tracking take the batch's signals in place of reading raw lines. Their state and rules (`RecordHarnessTitlesAsync`, `ConfirmTurnEndedAsync`, the pending-question correlation) are unchanged.
- `captureLoss` entries go through the existing `CaptureLossParser`.
- Held lines: a line whose write failed is held in `unstored_transcript_lines` as today, storing in place of the raw line everything the batch carried for that line: its events, title sightings, question entries, turn end and timestamp. A title-only line whose title write failed is held too, as it is today. The row is marked by a prefix so the retry feeds it back to the reader, where it passes the same checks as a fresh line. A raw line a client sends with that prefix is dropped with a warning, so it can never pass for a held canonical line. A canonical batch resolves a normalizer in one case only: to retry a raw line that an earlier raw batch left held. No new table. Limits and attempts are unchanged.

Fields the server owns. The ingress sets or removes them whatever the client sent:

- `$ingester_user_id` is always the authenticated caller. The raw path only fills it when missing; the ingress overwrites.
- Stored metadata comes only from the named event fields and allow-listed `vendorMetadata` keys, so `$source`, `$acpSeq`, `$captureRepair`, `$origin`, `$commits_observed` and `$copilotForkMarker` cannot be set by a client.
- The `capacitor` and `capacitor_turn` extension blocks are server-only and are removed from every event. A `SessionStarted` is then stamped with `StampUnset`, and loses the legacy owner fields.
- Accepted event types are the ones the client library can produce; a server-owned type such as a session end is refused.
- On user messages: the turn block is written by `UserMessageStamper`; a client-sent delivery block (`CapacitorUndeliveredBlock`) is removed.
- On attachments: the server recomputes the attachment id from the canonical session id it established and the message id and block index on the wire, as `ClaudeCodeNormalizer.DeriveAttachmentId` does, and refuses a batch whose id differs. Attachments are accepted on user messages only, and a client-sent attachment list or `StreamName` in a payload is removed. Blob streams are named by id alone (`Attachment-{id}`) and an existing one is reused without a check, so a client-chosen id would let one session claim another's.
- `eventId` is taken from the client. It is the dedup contract, and it only ever addresses the caller's own stream, which the ownership check has already established.

### 6. Reserved: the stamp

An event may carry an optional `stamp` object. Format 1 servers ignore it. The terminal steering work defines its content, its validation and the metadata key it is stored under; it must not need a new format number.

## Verification

- **Parity through the wire.** `LibraryParityTests` gains a mode that runs lines → shared client path → serialized canonical batch → the real ingress, and compares stored events with the same golden baselines and the same exemptions. This is the proof of criterion 1 for Claude, and it must include title events, turn-end confirmation and question events.
- **No normalizer.** A test that sends a Claude session through the ingress and asserts the Claude normalizer was never resolved (criterion 3).
- **No duplicates.** Send a session raw then re-import canonical, and the reverse, and switch path in the middle of a session in both directions. No event may appear twice or be missing. A switch at a turn boundary must give a stream identical to a single-path run; a switch inside a turn may differ only in the ways section 4 accepts (criterion 2).
- **Old client.** The existing raw-path suites stay untouched and green (criterion 4).
- **Brake.** With the switch off, a current client records a Claude session through raw lines; flipping it off mid-session makes the next batch fall back with nothing lost.
- **Trust.** One test per server-owned field above, sending a hostile value.
- **Restart.** Kill the recorder mid-session; after resume the stream equals an uninterrupted run.

## Order of work

1. `kcap`: wire types, the JSON writer, the Claude signal readers, the batch assembler, all without any caller.
2. `kcap-server`: pin that commit; add the ingress, the capability answer and the switch, off by default; add the parity mode.
3. `kcap`: the shared path, with every producer moved onto it and Claude in the canonical list.
4. Turn the switch on per environment, beta first.

Steps 1 and 3 can be one CLI release, since a client without a yes from the server stays on raw lines.

## Later vendors

Not built now, recorded so the contract is not a trap:

- Antigravity sorts by step, not line (`IStepOrderedProjection`, `CanonicalEvent.LineNumber`). The client must do that sort before it assembles.
- Antigravity, Copilot and Kiro append synthetic usage lines in high line-number bands. They are ordinary lines to the projection and need nothing new.
- Copilot needs its fork and start facts (`CopilotContext.Seed`), which the server holds on `SessionCopilotTranscript-{id}`. The client needs a way to get them.
- Codex, Copilot and Pi session starts are where the server must apply `StampUnset` itself.
- Cursor uses the acknowledged hub method and has no transcript file to replay from.

## Left to the builder

- Names of the batch type, hub method, route, capability call and configuration key.
- Which assembly hosts the shared path, given that import and the inline drain live in `Capacitor.Cli`, the recorder in `Capacitor.Cli.Recording` and the spool drain in `Capacitor.Cli.Core`.
- How a held canonical line is marked in `unstored_transcript_lines` (a column or an envelope).
- Chunk limits for canonical batches. Today's are 100 lines or 4 MiB.
- The exact `questions` shape, from what the tracker at `SessionWriter.TranscriptPipeline.cs:1414` reads.
- The `vendorMetadata` allow-list, from the keys the library writes.
- Whether the capability answer rides an existing call (`WatcherConnect`, `DaemonConnect`) or a new one.

## Calls made by the author, not the owner

The owner agreed to the overall behaviour in sections 3 and 4 as a summary. These details were not discussed and are the author's:

- Signals travel in the same batch as the events, as named fields.
- Capture-loss lines travel verbatim and the server keeps its parser, because they are kcap's own format.
- Held canonical lines reuse the existing table.
- The server derives attachment ids; the wire carries the block index only.
- Reading "more" in criterion 1 as excluding differences in usage and cost.
- The mid-session fallback on an "unsupported" refusal, and that anything the server does not understand refuses the whole batch.
- A switch from canonical to raw inside a turn may mis-stamp a usage or miss a retraction, as a server restart does today.
- State is rebuilt from raw lines, not redacted ones.
- The `format` number and the rule that the stamp must not need a new one.
- The stamp is out of this slice; only the slot is reserved.
- Cursor backfill, Muse Assistant and the ACP lane stay on their own paths.
