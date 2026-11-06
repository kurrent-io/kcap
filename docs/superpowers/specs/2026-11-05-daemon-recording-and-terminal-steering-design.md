# Daemon recording and terminal steering

Tracks AI-4410. Depends on AI-3706. Related: AI-4376, AI-4378, AI-4241, AI-4634.

## Terms

- **Dispatch:** one message a person sends from a remote surface, with the server's dispatch id.
- **Row:** one line of an agent's transcript file.
- **Recorder:** the code that tails a transcript and sends it to the server. Today that is a `kcap watch` process per session.
- **Stamp:** a dispatch id attached to a row as the recorder sends it.
- **Link:** a report that dispatch D became the row with uuid U.
- **Steering:** sending a message into a Claude session running in the person's own terminal.

## Why

Recording and hosting are separate processes that know nothing of each other. The daemon pastes a message into a hosted agent and knows who sent it, but never sees the transcript. The watcher sees the transcript but doesn't know who sent anything. So the server rebuilds authorship afterwards in `HostedAgentInputProjector`, matching text in FIFO order within a time window. AI-4410 lists how that keeps failing: redaction changes the line, attachment trailers force prefix matches, identical texts compete for one dispatch, local typing interleaves, and wake-ups need their own table.

Separately, people want to start an agent in a terminal and reach it later from somewhere else. HumanLayer's mod suggests how (https://kurrent.kcap.ai/pages/f0597f55c15040b0b61709c4bbbd9045).

The two meet here. Daemon recording was meant to fix authorship, because the daemon knows who sent what. A terminal session can't be moved to the daemon without ending its process, so this design separates three jobs that had been treated as one:

| Job | Hosted session | Terminal session |
|---|---|---|
| Recording | Daemon | Daemon, or the fallback watcher |
| Input | Daemon pastes into the PTY | Daemon hands the message to the mod, which submits it |
| Process | Daemon owns it | Stays in the terminal and never moves |

## Goals

1. The daemon records every session it can serve, with no per-session watcher process.
2. Every remote message has an exact author: by stamp for hosted PTY sessions, by link for terminal sessions. Remote terminal typing is the exception, because nothing proves which keystroke made which row.
3. A person can send a message to their own running terminal Claude session from another surface.
4. The FIFO matcher shrinks to the cases that can't be stamped. It can retire only if remote terminal typing gets an exact signal.

## Non-goals for the first version

- Moving a running process, or ending it and resuming it under the daemon. #2132 covers ended sessions.
- Mid-turn delivery to a terminal session.
- Steering any vendor other than Claude.
- Teammates sending into a terminal session.
- Answering a terminal session's permission prompts remotely. The session waits at the terminal, and the remote surface shows that it's waiting. This is the immediate follow-up.
- Interrupting a terminal session's turn remotely. This is part of the same follow-up.
- A server-side message queue. See the end of this document.
- Attachments and `@file` mentions in steering. A plugin prompt doesn't expand them, so steering is text only.
- Removing `kcap watch`.
- Removing the FIFO matcher. This work shrinks it.
- Stamping raw input from remote typing or an attached client.

## Anti-goals

These would make the work a failure even if it functioned.

- Recording depends on the mod or on the mod API in any way.
- A session goes unrecorded because the daemon is down.
- A session has two recorders, or none.
- One session's slow redaction or failure stalls recording for the others.
- A remote message reaches a terminal session without the person there being able to tell.
- Claude event ids change for rows already ingested.

## Agreed decisions

- The daemon records all sessions, not only hosted ones.
- A terminal session is never moved. It's steered in place.
- A remote message to a terminal session waits until the current turn ends, however long that takes.
- Hosted Claude and Codex keep PTY paste, because it lands mid-turn and Codex has no mod. The daemon stamps those dispatches. Hosted sessions don't load the mod.
- Terminal steering is Claude only, through a mod. The daemon passes it one message at a time, and it reports the link.
- A steered session registers with the server as an agent, on a runtime with no PTY whose stop kills nothing.
- `kcap watch` stays as a fallback for machines with no daemon and for sessions on a profile the daemon doesn't serve.
- If the mod breaks, steering stops and recording carries on.
- The FIFO matcher doesn't shrink until Claude and Codex both report exact authorship.
- AI-3706 is fixed first.
- Only the session's owner can send into a terminal session. Hosted agents keep their current access rules.
- Steering has one switch rather than one per session, stored with the daemon's consent settings, separate from launch consent, and off until set. `kcap setup` offers it. There is no per-session step, because a person can't turn on a per-session switch after leaving the terminal.
- Remote tool approvals and remote interrupt stay out of the first version and come straight after it.
- A message waiting in the daemon is rejected visibly if the daemon restarts or loses the server, and the sender sends it again.

## What the spike proved

Run on Claude Code 2.1.318, inside one live session, with a throwaway mod standing in for the daemon. Nothing crossed a process boundary.

- For `$.prompt.submit({ text, asUser: true })`, the uuid on `session.append` equals the uuid on the transcript line. Claude's first event for a row uses that uuid as its id (`ClaudeTranscriptEvents`, `Emitter`), so the server can find the event directly.
- The row stores `origin: {"kind":"plugin","name":…,"asUser":true}`. A typed prompt stores `human`.
- Two prompts submitted together shared one turn but stayed separate rows, in order, even with identical text. A dispatch maps to one row, but not to one turn.
- A plugin prompt waits for idle (`queuePriority: later`) and isn't absorbed mid-turn. Typed prompts entered at the next tool boundary and overtook it. In the test the dispatch took 46 seconds to land.
- In these tests the submit promise resolved when the row landed. The API types say it can also resolve once a prompt is queued behind a running turn, so this design judges delivery by the row, not the promise.
- `prompt.submit` from a `tool.call` hook is refused. From `$.clock.every` it works.
- Submit takes no id and returns no uuid, so the mod ties rows to dispatches itself, on `session.append`.
- A mod's own `prompt.submit` hook doesn't fire for its own prompts, so `session.append` is the only place it sees them.
- Ingest already keeps these rows. `UserMessageClassifier` has no `plugin` case, so they classify as `Person`, and the author comes from the link.

Not tested: a real daemon-to-mod round trip, a mod installed as a plugin rather than loaded from the dev folder, and Esc, exit, compaction, `/clear` or resume while a dispatch is waiting. The mod API is marked early access and may change without notice.

## Part 1: the daemon records

**Shared code.** `WatchCommand.cs` is 5,370 lines in the `kcap` exe and also handles titles, PR detection, subagents and session-end decisions, with heavy per-vendor branching. All 12 vendors' hook commands spawn it. Extract it whole, not by vendor. The daemon references only `Capacitor.Cli.Core` and `Capacitor.Remote.Models`, so the recorder and what it depends on (`Capacitor.Cli/Capture`, parts of `Capacitor.Cli/Harness/<Vendor>`) must move to code both the daemon and `kcap watch` can reference. `kcap watch` then runs that same code for one session.

**One process, many sessions.** The recorder must not assume it owns the process. Each session has its own state, its own failures and its own redaction retries. A redaction timeout holds that session's source only.

**Hosted sessions.** The daemon records a hosted session from launch. A hook from a hosted session never spawns a watcher on a daemon that has the new capability, because a hosted session on a watcher would go unstamped for the rest of its life.

**Handoff for terminal sessions.** `WatcherManager.EnsureWatcherRunning` is the one place hooks ask for a recorder. It first asks the daemon serving the hook's own profile and server to take the session, over local control, behind a new capability. If the daemon is missing, older, or serves another profile, it spawns `kcap watch` as it does today.

**One recorder per source.** A source is one transcript, keyed as `WatcherTracker.GetKey` keys it, so a subagent or child transcript is its own source. A claim per source decides who records it. `WatcherTracker.Register` silently replaces an older connection, so the server won't catch a double recorder and the claim has to hold on the machine.

The claim is a new file. It can't reuse the watcher pid file, because `KillWatcher` signals and then force-kills whatever pid that file names, and that must never be the daemon's. The existing per-key spawn lock guards it.

**Stop and final drain.** `EnsureWatcherRunning` isn't the only path to the server. Session-end and subagent-stop hooks for at least seven vendors call `WatcherManager.KillWatcher` and then `InlineDrainAsync`, which reads the server's watermark and posts batches itself, with no claim and no stamps. An unstamped drain that wins would make the server discard the daemon's stamped copy. `CleanupCommand` and `Program.cs` stop watchers too. Both methods become claim-aware inside `WatcherManager`, so vendor call sites don't change:

| Who holds the source's claim | `KillWatcher` | `InlineDrainAsync` |
|---|---|---|
| A `kcap watch` process | Stops it, as today | Drains, as today |
| The daemon, heartbeat fresh | Signals nothing | Asks the daemon to finish that source, and waits within the hook's budget |
| The daemon, heartbeat stale, terminal session | Signals nothing | Takes the claim and drains, as today |
| The daemon, heartbeat stale, hosted session | Signals nothing | Does nothing. The daemon's catch-up finishes the source, so its rows keep their stamps |
| Nobody | Nothing to stop | Drains, as today |

Finishing a source runs the recorder's own final drain for that key and touches no other source, so a subagent stop leaves its parent running and a session end leaves other sessions running. If the hook's budget runs out, the daemon still finishes, because the hook only asked.

**Older binaries.** A hook binary from before this work doesn't know claims, so it can start a watcher or drain beside the daemon. The server's watermark and event ids keep the data intact. The cost is a row with no stamp, which falls back to the FIFO matcher. `kcap import` stays a manual repair and doesn't take claims.

**Daemon restarts.** The daemon's claim carries a heartbeat, as a watcher's does. A hook spawns a watcher for a daemon-claimed session only once that heartbeat has been stale for longer than a normal restart takes, so a daemon update doesn't push busy terminal sessions onto watchers. After a restart the daemon resumes each session in its persisted list from the line `WatcherConnect` returns, skipping any session a live watcher has claimed.

**Outages.** A longer outage moves a terminal session to a watcher, and it stays there until it ends. There is no takeover: steering doesn't need the daemon to be the recorder, and a line-position handoff between two recorders isn't worth its risk for a case this rare. An idle session with no hooks firing has nothing new to record, so its gap closes on restart.

**Hub.** The daemon uses the same hub (`/hubs/sessions`) and the same transcript methods. `StopWatcher` is pushed to a connection with no session in its payload, which works only while a connection carries one session, so it needs to name the session in a way older watchers still accept.

**Rollout.** Hosted Claude, then hosted Codex PTY, then terminal Claude, then the other vendors.

## Part 2: stamps for hosted PTY sessions

As AI-4410 describes. `TranscriptBatch` gains an optional per-line dispatch id. The hub method takes a single record, so this is an added field and older CLIs keep today's path. The server stores the id in the event's metadata, and the attribution projector uses it directly, falling back to the FIFO matcher when it's absent. The desktop can then look up every harness by dispatch id, as AI-4378 does for journal-sourced ones.

**Exact or nothing.** The daemon writes a stamp only when it's sure. A row it can't place goes unstamped and the FIFO matcher handles it. A wrong stamp is worse than none, because the projector trusts a stamp without checking it.

**Input log.** The daemon knows every message it pastes. Before writing a remote message or a launch prompt to the PTY, it appends an entry to a per-session log holding its position, dispatch id, sender and a hash of its text, marked Pending. After the write it marks the entry Written. The log lives in the daemon's owner-only state directory and holds no message text.

**Launch prompts.** The server mints an id in `RegisterLaunchPrompt`, but `LaunchAgentCommand` doesn't carry it. It needs to, so the launch prompt's entry has its dispatch id. AI-4634's bug comes from this path.

**Raw input.** Remote typing and an attached client arrive as bytes, and `IsSubmit` only sees a CR or LF. That can equally answer a dialog, submit an empty composer or sit inside a multiline paste, so an Enter doesn't prove a row. Raw input never makes an entry and never gets a stamp, and those rows stay with the FIFO matcher. Anything else written to the PTY that could submit, whether a raw chunk holding a CR or LF or a special key (`HandleSendSpecialKey`), suspends stamping instead.

**Local messages.** The desktop composer sends whole messages through the `SendText` and `SendTextWithAttachments` frames and `DeliverInputAsync`, with no dispatch id. The exception is `TerminalChatInput`, which sends plain text as raw bytes through an attached terminal, so its first message would suspend stamping for the whole session. It moves to `SendText` as well. A local message makes an entry with its text and the local owner as sender, and no dispatch id.

**Local marker.** A row matched to a local entry can't simply go unstamped. A row with no stamp is eligible for the FIFO matcher, which could give it to a pending remote dispatch with the same text. So a line gains an optional local marker beside the optional dispatch id. A marked row belongs to the owner, and the FIFO matcher skips it and consumes no pending dispatch for it. A line with neither stays eligible, which older CLIs rely on.

**Match rule.** While stamping is active, each person-authored row is compared with the oldest Written entry, using the raw line before redaction. For Claude a person-authored row is a `user` row with `human` origin or a `queued_command` attachment. Other vendors' row kinds are the builder's to map. If the hash matches, the row takes that entry's dispatch id, so identical texts are told apart by position. If it doesn't, or there's no entry, the row goes unstamped and stamping is suspended.

**Suspension.** While stamping is suspended, rows go unstamped and the FIFO matcher handles them. Idle and end of file don't end it. They don't prove that earlier input can't still produce a row, because the PTY, turn reporting and transcript writes run independently, and a late row could then consume a later entry with the same text. Suspension ends only when the session's process has exited and a new one replaces it, because a dead process can't add a row. The daemon then discards unmatched entries and stamps from the first line after the replacement. The suspended state persists in the log, so a daemon restart doesn't clear it.

**Replay.** Before retiring a matched entry, the daemon persists the match: source line number, line hash and dispatch id. A row recorded again after a crash or a `WatcherConnect` rewind takes its stamp from that record and consumes no entry. A match record goes only once the server has acknowledged that line, including lines spooled for later and lines in the final drain, so a session's log outlives the session until its source is finished.

**Crash.** An entry still Pending at recovery doesn't show whether the PTY write happened, so the daemon suspends stamping for that session.

**Proof.** Part 2 opens with AI-4410's measured spike, on real hosted Claude and Codex sessions against today's FIFO attribution. It must show one row per pasted message, in order. It covers identical text from two senders, a mid-turn paste, delayed transcript writes, slash commands and skills, attachment trailers, an Enter that answers a dialog, an empty submit, a multiline paste, repeated submit Enters, a daemon killed between the Pending entry and the PTY write, one killed between the write and the row, replay after a match but before the server accepted the line, a delayed row from raw input arriving after apparent idle and after an identical message, and a local and a remote message with identical text, with the remote dispatch pending on the server and the local row ingested first. The local row must stay the owner's, and the dispatch must link only to its own row. It also measures how many sessions end up suspended, because routine suspension would leave little for stamps to do. Where order or text doesn't hold, the rule narrows to what does, and those cases stay on the FIFO matcher. A mid-turn paste is expected to land as a `queued_command` attachment with `human` origin. That hasn't been run.

## Part 3: steering a terminal session

**Flow.**

1. At session start the mod contacts the daemon. If there's no daemon, the switch is off, or the daemon doesn't serve this session's profile, the mod stays inert.
2. The daemon registers the session with the server as an attached agent.
3. The owner sends a message. The server applies the owner rule, reserves a dispatch as it does for hosted agents, and pushes it to the daemon. The dispatch is now Held, and the remote surface shows it as waiting for the turn to end.
4. When the mod has nothing outstanding it asks the daemon for the next dispatch. The daemon asks the server to confirm the handover, and only then passes it to the mod. The dispatch is now With the mod.
5. The mod keeps the dispatch somewhere that survives a module reload and a session change, and submits it from a timer with `asUser: true`.
6. The mod's `session.append` hook sees a `user` row with its own plugin origin and takes the uuid. The row's text must equal the dispatch's text.
7. The mod reports the link and the daemon forwards it to the server. The dispatch is now Delivered, and the mod can ask for the next one.
8. The server attaches the dispatch to the event whose id is that uuid.

**Link ordering.** The row comes through the recorder and the link through the daemon, so either can reach the server first. The server must accept both orders. This path doesn't depend on who records the session, so a session on the fallback watcher can still be steered.

**Dispatch states.** The server owns the state. A dispatch is never silently lost, and it's never delivered twice.

| State | Can it still reach the agent? | Sender sees |
|---|---|---|
| Held: in the daemon's memory | Only after a confirmed handover | Waiting for the turn to end |
| With the mod | Yes | Waiting for the turn to end |
| Delivered: row linked | It has | Delivered |
| Rejected | No | Not delivered, send again |
| Unconfirmed | Possibly | Delivery unknown, check the session before sending again |

Only a dispatch that cannot land is Rejected, and only Rejected tells the sender to send again.

| From | To | When |
|---|---|---|
| Held | With the mod | The server confirms the handover. It refuses for a dispatch it has already rejected, and the daemon drops that dispatch |
| Held | Rejected | The daemon's connection drops, the attachment lapses or ends, or the process has an Unconfirmed dispatch. The server does this itself, so a hard-killed daemon needs to report nothing |
| With the mod | Delivered | The link arrives |
| With the mod | Rejected | The submit returned `drop`. Nothing else can prove the prompt won't land |
| With the mod | Unconfirmed | The session goes idle and no matching row appears within a grace period, or the session ends, or the attachment lapses, or the daemon stays away past a grace period |
| Unconfirmed | Delivered | A late link arrives. The sender has been told to check, so this corrects the record |

The mod holds one dispatch at a time, and no handover is confirmed while one is With the mod or Unconfirmed, so a plugin-origin row belongs to at most one dispatch. This barrier belongs to the Claude process, not the session id, so it holds across `/clear` and resume. An Unconfirmed dispatch stops steering for that process, and new messages are rejected with that reason until a late link arrives or the process exits. A row that matches nothing outstanding is reported as unmatched and stays unattributed.

The daemon drops its Held dispatches when its connection drops. It can't confirm a handover while disconnected, so nothing it's holding can reach the mod after the server has rejected it.

**Session changes.** An attached agent belongs to one session id. `/clear` and resume end that session while the process carries on under a new id, and no `session.start` fires. The mod sees this on `session.end`. Held dispatches are rejected, because they were written for the old conversation. A dispatch With the mod goes Unconfirmed, because it might still land in the new conversation.

The mod then attaches again as a new agent under the new id, naming the agent it replaces so the server carries the barrier across. With nothing outstanding, steering carries straight on. With an Unconfirmed dispatch, the new agent takes no input until a late link arrives or the process exits. If the proof shows a session change always discards queued prompts, a dispatch With the mod can be Rejected at the change instead, and the barrier clears.

**Attached agent.** A new `IHostedAgentRuntime` with no PTY. Stop removes the attachment and touches nothing in the process. It offers no terminal output, resize, special keys or raw input. It's never resumed or restarted by the daemon, and it doesn't hold up a daemon restart or update. The rail and agent lists must not imply the daemon hosts it. `PersistedInterruptResolver` must not treat it as reachable, or remote answers to a permission prompt would be routed to a session that can't take them.

**Daemon restart.** The daemon persists its attachments and reports them on reconnect, so `ReapAbsentAgentsAsync` doesn't fail them before the mod returns. An attached agent's id is stable across restarts. A dispatch With the mod can't be recalled, so the mod keeps its unreported link and retries it, and the server accepts it after a restart.

Held dispatches live only in the daemon's memory. Today the server counts a dispatch as delivered once it's pushed and nothing redelivers it, so the server has to track Held and With the mod separately for attached agents. The connection dropping is what rejects a Held dispatch, which covers a hard kill and a failed report, because the daemon's own rejection call is best-effort. An attached agent doesn't hold up a daemon update, so an update can bounce a waiting message.

**Owner rule.** Enforced in `HostedAgentAccessPolicy`: an attached agent takes input from its owner only, whatever grants exist.

**Switch.** `consent.json` belongs to one daemon and that daemon is its only writer, so the switch is per daemon, which means per profile. A person with one profile has one switch per machine, as agreed. A person with two turns it on for each, and turning it on for one doesn't open the other's sessions. A missing or corrupt value reads as off. That's the opposite of launch consent, which degrades to allow so an update can't brick a daemon. No existing behaviour depends on steering being on.

**Marker.** The person at the terminal sees that a message came from a remote surface, at the moment it arrives.

**Mod to daemon.** The binary local control socket stays as it is. The daemon adds a small HTTP endpoint on its own owner-only Unix socket, because the mod's `$.http.fetch(url, { socketPath })` speaks HTTP. The socket's permissions are the authentication, so no token enters the session. A credential must never reach the environment the model's shell can read.

**Packaging.** The mod ships as its own plugin rather than as a `modules` entry in `kcap/hooks/hooks.json`. Nobody has checked what a Claude Code build that doesn't know `modules` does with that file, and a load failure there would take the recording hooks with it. The first time a person turns a switch on, the plugin is installed and enabled. It then stays installed, and it's inert for any daemon whose switch is off.

## Constraints

These are repository invariants the work must not undo (`CLAUDE.md`).

- Transcript event ids are a persistence contract. The daemon derives them exactly as the watcher does.
- A redaction timeout holds its source and never drops or marks the line.
- Redaction rewrites decoded JSON string values, never the serialized line.
- A 401 spools, and only a rejected payload drops. Spooling a transcript tail keeps working from the daemon.
- Local control IPC is append-only and capability-gated.
- Agent-owned files are read with `FileShare.ReadWrite`, or Windows stops the agent writing its own transcript.
- The PTY read loop never awaits a consumer. Recording must not add one.
- The daemon never stops a process in its own group.
- The daemon keeps one profile for its whole life (`DaemonConfig.cs`).
- The server drops a line numbered at or below one it already took.

AOT rules apply to anything added to the daemon.

## Order of work

1. **AI-3706.** A long transcript must cost nothing while idle before one process tails all of them.
2. **Steering proof, throwaway.** A real daemon-to-mod round trip with the mod installed as a plugin, then Esc, exit, compaction, `/clear` and resume with a dispatch waiting. It must pass before Part 3 is built. If it fails, Parts 1 and 2 still stand.
   - **Pass:** every dispatch ends in the state the tables give. A Rejected dispatch never produces a row, no dispatch produces two rows, and Unconfirmed is rare. The run includes a daemon hard-killed between push and handover, a row arriving after Unconfirmed, and a resend after Unconfirmed. No row from an old session can be linked to a dispatch sent after `/clear` or resume.
   - **Fail:** Unconfirmed turns out to be routine, for example because Esc routinely discards a queued prompt with no signal. Steering would stop in those sessions, so Part 3 then needs rethinking before it's built.
   - **Windows:** check `socketPath` there too. If it doesn't work, Part 3 ships on macOS and Linux first, and Windows waits for its own transport.
3. **Extract the recorder**, with `kcap watch` on the shared code and no behaviour change.
4. **Hosted recording and stamps** (Parts 1 and 2), hosted Claude first, measured against today's FIFO attribution on the same sessions.
5. **Terminal and other-vendor recording.**
6. **Steering** (Part 3). It depends on steps 1 and 2 only, so it can run alongside 3 to 5.
7. **Remote approvals and interrupt**, immediately after steering. HumanLayer's mod is the prior art: it asks the web and the terminal together and takes whichever answers first, and it interrupts with `$.turn.abort`. This step removes the `PersistedInterruptResolver` guard.

## Later

A server-side message queue drained at turn end, with daemons collecting unacknowledged dispatches on connect (ideas 2 and 3 on the HumanLayer research page). It would keep a waiting message through a daemon restart, so the rejection above could go. It would also give every vendor one queueing behaviour, and PTY paste would stay as the "send now" path. It's separate work and needs its own design.

## Left to the builder

- Where the shared recorder code lives and how it's split.
- The claim mechanism, as long as both recorders honour it.
- How the daemon persists its recording list.
- Polling or long polling between mod and daemon, and the endpoint's wire format.
- The server-side storage for links.
- How long a lapsed attachment takes to detect.
- The marker's form, and the wording on remote surfaces.

## Calls made while writing this, not yet agreed

- A hosted session never falls back to a watcher on a daemon with the new capability.
- A hook waits out a grace period on a stale daemon claim before spawning a watcher.
- There is no takeover from a watcher back to the daemon.
- Launch prompts are stamped. Raw input isn't, so the FIFO matcher keeps those cases and can't fully retire.
- The switch is per daemon, so per profile.
- A missing or corrupt switch reads as off.
- `/clear` and resume end the attached agent, reject its Held dispatches and attach a new one.
- If `socketPath` doesn't work on Windows, Part 3 ships on macOS and Linux first.
- The mod ships as its own plugin, and it stays installed while inert.
- The steering endpoint gets its own socket.
- The steering proof runs before the recorder is extracted.
- A dispatch that might still land is Unconfirmed, never Rejected, and it stops steering in that process until it resolves.
- A handover needs the server's confirmation before the mod sees the dispatch.
- The daemon stamps only when sure, and a mismatch or raw Enter suspends stamping until the session's process is replaced.
- `TerminalChatInput` moves plain text from raw bytes to `SendText`, so the composer never suspends stamping.
- A line can carry a local marker, which keeps it away from the FIFO matcher.
- The input log persists a hash of each submission rather than its text.
- Claims are a new file, separate from the watcher pid file.
- Older hook binaries can record beside the daemon, at the cost of an unstamped row.
