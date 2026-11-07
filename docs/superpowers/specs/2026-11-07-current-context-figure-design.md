# Current context figure

Issue: kurrent-io/kcap#2481 (Linear AI-5296)

## Intent

The session sidebar in the desktop app and in the web UI shows a session's context use. People watch it on a live session to decide when to compact by hand, which they do once it passes about 80%. The figure is the session's peak, a high-water mark that never drops, so after a compaction it keeps reading as nearly full and cannot say how full the context is now

This design adds a current-context figure that follows the main conversation's latest reading and drops at a compaction, colours it as a warning from 80%, and puts a small button beside it that sends `/compact` to a hosted agent once it crosses that line. The peak stays as a secondary figure

## Goals

- On a live session, the figure shows how full the main conversation's context is now, for Claude, Codex and any harness that reports a reading
- After a compaction the figure stops showing the old level at once: Claude's shows the new level from the compaction record, Codex's shows that a compaction happened until its next reading arrives
- From 80% the figure is in the warning colour, and on a session kcap hosts a button sends `/compact` to the agent
- The peak stays visible on both surfaces

## Non-goals

- A configurable threshold
- Compacting a session kcap does not host, such as one the user started in their own terminal: kcap cannot type into it
- Notifications or alerts when the figure crosses 80%
- Re-projecting sessions already ingested: they keep showing the peak only
- Showing a subagent's own context

## Anti-goals

Any of these makes it a failure even if it otherwise works:

- The figure still reads the pre-compaction level after the user compacted
- A subagent's reading moves the figure, so compacting the main conversation would not lower it
- The button sends anything to a session that is not live and hosted, or skips the path a typed message takes (queueing while the agent is busy, the PTY paste delivery)
- Older sessions, older summary events or an older server stop rendering the peak

## Where the readings come from

Checked against local transcripts and the current code of both repositories

**Claude**

- Every main-conversation assistant reply carries usage. Occupancy is input + cache read + cache creation tokens, the measure the peak already uses
- At every compaction, manual or automatic, Claude writes a record with `type: "system"`, `subtype: "compact_boundary"` and a `compactMetadata` object holding `trigger` (`"manual"` or `"auto"`), `preTokens` and `postTokens`. In local samples `preTokens` matched the last reply's occupancy to within 1.5k tokens, and `postTokens` was 34k to 52k after 200k to 650k. The record is followed by a user message marked `isCompactSummary`
- kcap-server's live Claude projection, `ClaudeCodeNormalizer`, drops every `system` record (its `NoiseTypes`), so neither number reaches the server today. The CLI's Claude projection in `Capacitor.Models.Transcripts` skips the record too
- The PreCompact hook reaches the server as `PreCompactEvent`, which carries only the session id. This design does not need it

**Codex**

- Every response's `token_count` reports `last_token_usage` and `model_context_window`, and the projection already turns it into `$usage` metadata carrying the window
- A compaction is a `compacted` rollout record, projected as `ContextCompacted`. It carries no count for the compacted context. A `token_count` with zero usage follows immediately, and the next real reading comes with the next response, about 6 seconds later in local samples, because Codex compacts in the middle of a turn and carries on

**Others**

- Hosted ACP agents report `SessionContextObservedEvent` with used and window tokens
- Kiro reports a percentage only, on assistant events and on `KiroUsageBackfilledEvent`

## Behaviour

### The current reading

- It is taken only from events on the session's root stream. Subagent events reach the session's stats only through `AgentSummaryComputedEvent` (`SessionStats.MergeAgentSummary` and `Aggregate`), which max-merge the peak; the current fields stay out of that merge, and `CloneStatsOrNew` carries them over
- The projector's live SQL token path tracks no peak today, so the current reading is new work there, not a copy of an existing branch
- Each reading replaces the previous one, with no max-merge: tokens, model and window when reported, or the percentage for a percentage-only harness
- Usage echoes (`IsUsageEcho`, the Codex cluster siblings) and zero readings are ignored, as the peak ignores them
- A Claude `compact_boundary` sets the reading to its `postTokens`, keeping the window of the reading it replaces
- A `ContextCompacted` without counts clears the reading and marks the session as compacted and awaiting a reading. The next non-zero reading clears the mark
- A reading that arrives after the session ended never reactivates it, the rule `HandleSessionContextObserved` already follows
- The figure divides by the window the peak already uses: the one the harness reports (Codex, ACP), or for Claude the pricing table's window for the reply's model (`UsagePricing.ContextWindow`). Each model Claude Code offers comes with a single window, so there is no 200k-or-1M choice per session to detect. If a model again offers both, the 80% line goes wrong for one of them and this needs revisiting

### Ended sessions

- They show the last reading with the peak alongside, with no warning colour and no button
- A session with no current reading, because it was ingested before this ships or its harness reports none, keeps the peak-only display labelled as the peak

### Display

**Desktop**, in the work-context pane's session usage grid:

- A `CONTEXT` row above `PEAK CONTEXT` shows the current reading in the peak's format, such as "37k / 1M (4%)", or "compacted" while awaiting a reading
- From 80%, on a live session, the value is in `KcapWarning*`, the needs-you colour. The label never takes it
- The compact button sits at the end of that row
- The pane reads the session summary on a 30-second poll. It also re-reads it when a hosted agent's turn ends or its status changes, and polls every few seconds after a compact click until a compaction is seen, so the drop shows within seconds rather than half a minute. A Codex "compacted" state that lasts a few seconds may never be seen, which is fine *(my call)*

**Web**:

- The session overview gets a "Context" figure for the current reading beside "Peak context"
- The sessions list row's meter, the web sidebar, follows the current reading when there is one and the peak otherwise, and its tooltip says which
- Where a figure adds up a chain of sessions (`SessionStatsAggregator`), the current reading is the newest session's, never a merge *(my call)*
- The current figure's tone is warning from 80% and error from 90%. When a current reading exists, the peak figure is neutral, since a red peak on a compacted session reads as an alarm *(my call)*

### The compact button

- It shows when the session is live, kcap hosts it, and the current figure is 80% or more. It is hidden while the session is awaiting a reading after a compaction
- On the desktop, a session counts as hosted when the app can send it a message through its chat tab
- On the web, the only page that can send to a hosted agent is the agent page (`AgentDetailPage`, through `AgentInputSlot`), so the button goes there, beside a current-context figure added to that page, under the same rules the input follows (write access, running, connected, not a flow participant). The session overview and the sessions list show the figure without a button *(my call)*
- A click sends `/compact` as the agent's next message through the same path a typed composer message takes: on the desktop the chat tab's composer send with its queue, on the web the hosted agent input. A busy agent gets it when its turn ends, like any queued message
- After a click the button is disabled until a compaction is seen or the figure drops below 80%, so a second click cannot send it twice
- It is a Lucide icon through `AppIcon` with a named mapping, styled `kcapGhost`, with the tooltip "Compact context". The web uses its own equivalent
- No confirmation step
- Claude and Codex only. Another harness gets the button once it is known to accept `/compact`

## Constraints

- **Transcript event ids are a persistence contract.** The new Claude event comes from a line that produced no event before, so it needs its own stable id that leaves every existing id unchanged. Already-ingested sessions are never re-projected
- **Projector and fold agree.** `SessionProjector` (live) and `SessionSummaryFold` both maintain stats and must produce the same current reading. The summary events that restore the peak fields on replay carry the current fields too, as optional properties: an older summary event without them still replays, and the session falls back to the peak
- **Compaction counts are optional fields.** `ContextCompactedEvent` gains optional pre, post and trigger fields in both copies, kcap-server's `Capacitor.Server.Core/Events/CodexEvents.cs` and kcap's `Capacitor.Models.Transcripts`, so Codex and Pi events without them still deserialize
- **Older clients and servers keep working.** `SessionStatsView` and `ActivitySessionStats` on the server, and `SessionStatsDto` in the desktop app, gain optional current fields. An older server sends none, and the desktop app then shows the peak only
- **A hosted agent's input never goes on a command line.** The slash command reaches the agent through the composer's PTY paste delivery
- **Status colours mean status.** The warning colour goes on the figure's value and the button only

## Decided

- The figure follows the main conversation only *(user)*
- It drops at a compaction: Claude from `postTokens`, Codex shows "compacted" until its next reading *(user)*
- Codex is in scope with Claude *(user)*
- The window comes from where the peak gets it, with no per-session window detection, because current Claude models each have one window *(user)*
- The 80% line is fixed *(my call, user agreed)*
- The button appears only on live hosted sessions, sends without confirmation, and ships in the desktop app first and in the web wherever it can already send *(user agreed)*
- An ended session shows its last reading and the peak, with no colour and no button *(my call, user agreed)*

## Left to the builder

- Field names, and where exactly the root-stream check goes
- Whether Claude's compaction rides `ContextCompacted` with the new optional fields (recommended) or its own event type
- Whether the CLI's `Capacitor.Models.Transcripts` Claude projection also has to emit the event, which depends on whether it feeds any live path
- The icon
- How the web shows the peak, as a separate figure or in the current figure's tooltip, as long as it stays visible
- How each agent treats `/compact` arriving mid-turn. If one rejects or mangles it, the button stays disabled while that agent is busy

## Delivery

1. kcap-server: the Claude normalizer emits the compaction counts, and the current reading lands in the projector, the fold, the summary events and the API, with the web figure, list meter and tones
2. kcap-server: the agent page's figure and the web compact button
3. kcap: the desktop `CONTEXT` row, its colour, the refresh triggers and the compact button, closing #2481

Only the last PR may carry AI-5296 in its branch, title or description: Linear completes an issue once every PR linked to it has merged. This file rides the kcap PR, since kcap is where the designs live, and the signed-off text goes on AI-5296 as a comment, which is the durable record *(my call)*
