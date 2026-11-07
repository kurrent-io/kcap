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
- There are no new Postgres tables. The only schema change is two nullable columns on `hosted_agent_pending_dispatches`.
- The FIFO matcher stays for raw terminal typing, which can't be stamped.
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

The spike didn't test a real daemon-to-mod round trip, a mod installed as a plugin rather than loaded from the dev folder, or Esc, exit, compaction, `/clear` or resume while a dispatch is waiting. The steering proof covers those. The mod API is marked early access and may change without notice.

**The steering proof.** Run on Claude Code 2.1.318 on macOS with Haiku, using real Claude processes in tmux, a Node stub for the daemon and server on a Unix socket, and a mod installed as a plugin from a local marketplace at project scope. There were 80 runs and 102 dispatches: 56 Delivered, 31 Rejected and 15 left Unconfirmed. A separate check against the raw transcripts found that no Rejected dispatch produced a row, no dispatch produced two rows, and every link matched its row's uuid, session and text. The stub implements the tables, so a dispatch couldn't end outside them. What the runs show is that real processes give a signal for each transition.

- `$.http.fetch` with `socketPath` works from an installed plugin, and a round trip's link uuid equals the transcript line's uuid.
- Esc during a turn removes a queued plugin prompt, and the submit resolves `{ drop }`, 7 of 7. That includes double Esc and the first Ctrl-C. A typed prompt queued beside it still runs.
- After that, every plugin prompt drops with `the session was stopped and nobody has sent input since`. This outlasted 45 seconds. A typed prompt or `/clear` lifts it. `$.turn.abort` didn't cause it, in one run.
- Esc at a permission dialog refuses the tool, but the turn ends `answer`, not `aborted`, and the queued prompt runs, 3 of 3. Those runs sent no later dispatch, so the refusal above wasn't tested there.
- A prompt queued ahead of a session change isn't discarded. In 17 of 17 runs it was queued about a second before a `/clear` or `/resume` typed mid-turn, and it landed in the new conversation, including when "send now" aborted the turn. The link arrived 36 to 61 ms after `session.end`, apart from one run where a dialog held it for 1.5 seconds. The reverse order, and a submit in the milliseconds after a turn ends, weren't run.
- A mod that takes a dispatch only when no turn is running avoids that case. Mid-turn `/clear` (2), resume and exit ended Held to Rejected with nothing submitted. This narrows the window and doesn't close it, because a queued command or typed prompt can still run first when a turn ends.
- Exit produces no row and no `drop`. `session.end` fires with the dispatch in hand on every exit except `kill -9`, where only a lapse can tell. `--resume` doesn't restore the prompt, 2 of 2.
- Compaction keeps the prompt and produces one row, after the boundary, 4 of 4. That includes auto-compaction inside a turn.
- While a dialog is open at idle (`/config`, the resume picker), the queue stalls with no signal, and the row lands when the dialog closes.
- `$.prompt.submit` refuses text that starts with `/`, after any leading whitespace, and the command doesn't run. Other shapes land with exact text: multi-line, padded, unicode, `@file`, `!`, 20,000 characters.
- A `UserPromptSubmit` hook that blocks a prompt gives `drop` with the hook's reason.
- `$.state` survives a reload and compaction, and `/clear` and resume wiped it in all 15 runs that read it afterwards. `$.store` survives all of them. It's one file per plugin shared by every process, so a process keys its own entries. Nothing removes old keys, and it caps at 4 MiB. Two processes writing at once lost nothing, in one run.
- `session.end` fires on `/clear` and resume. `session.start` doesn't, and the module isn't reloaded. The new id arrives on `classic.SessionStart`. On an in-process resume, `$.session.id()` inside that hook still gives the old id, 9 of 9.
- Rows already in a transcript don't reach `session.append` on any load.
- A second process can resume a live session and keep the same id. Neither mod is told. Both write one transcript file, which forks.
- A mod can't bind or pass `$`, so its helpers are closures in `session.start`.

All 15 dispatches left Unconfirmed were forced: 9 process exits, 3 dialogs left open, and 3 from a bug in the proof's mod, fixed and rerun as Rejected. On 30 days of one machine's transcripts, 96 of 44,202 queued prompts were orphaned at end of file, the closest measure of exiting with something queued, and `/clear` appeared once in 23,329 prompts. That's one person's use, and there's no data on resume or on how long people leave a dialog open.

Not tested: `socketPath` on Windows or Linux, a real daemon or server, a session with other plugins or kcap's hooks loaded, a handover refused for a dispatch already rejected, a daemon away past a grace period, and the moment between a turn ending and a queued command running.

## Part 1: the daemon records

**Shared code.** `WatchCommand.cs` is 5,370 lines in the `kcap` exe and also handles titles, PR detection, subagents and session-end decisions, with heavy per-vendor branching. All 12 vendors' hook commands spawn it. Extract it whole, not by vendor. The daemon references only `Capacitor.Cli.Core` and `Capacitor.Remote.Models`, so the recorder and what it depends on (`Capacitor.Cli/Capture`, parts of `Capacitor.Cli/Harness/<Vendor>`) must move to code both the daemon and `kcap watch` can reference. `kcap watch` then runs that same code for one session.

**One process, many sessions.** The recorder must not assume it owns the process. Each session has its own state, its own failures and its own redaction retries. A redaction timeout holds that session's source only.

**Hosted sessions.** The daemon records a hosted session from launch. A hook from a hosted session never spawns a watcher on a daemon that has the new capability, because a hosted session on a watcher would go unstamped for the rest of its life.

**Capture scope.** The daemon never records what a hook would refuse. A hook's capture gate decides for a profile with an exclusion configured, so the daemon takes no such session at launch, and takes it only when the hook hands it over. A profile it can't read counts as one with an exclusion.

**Handoff for terminal sessions.** `WatcherManager.EnsureWatcherRunning` is the one place hooks ask for a recorder. It first asks the daemon serving the hook's own profile and server to take the session, over local control, behind a new capability. If the daemon is missing, older, or serves another profile, it spawns `kcap watch` as it does today.

There is no agent to tie a terminal session to, so the hook's own resolved paths are the authority. The hook names its profile, server, config directory and claims directory, the Claude config directory its account resolves the transcript to, and the Claude process running the session, by pid and start token. The daemon takes the session only if the first four are its own, the transcript, with its links followed, is that session's or subagent's under that Claude directory's projects, and the process is still running. The socket is the user's own, so whoever asks could start a watcher on any file; what the check buys is that the daemon records what that hook's watcher would have, to the same server, under the same claims and disable markers, and reads the file Claude writes rather than whatever a link later points at.

A terminal session has no agent exit to end it, so its process does. The daemon looks at each terminal source's process every few seconds, as a watcher's parent monitor does, and finishes the source once that process has gone, if a session-end hook didn't first. It never signals that process. A terminal source's claim is beaten only while the daemon's connection to its server is up: a terminal session costs nothing on a watcher, which may reach the server the daemon can't, so an outage longer than the grace moves it there. A hosted source keeps beating through an outage, as it must for its stamps.

**One recorder per source.** A source is one transcript, keyed as `WatcherTracker.GetKey` keys it, so a subagent or child transcript is its own source. A claim per source decides who records it. `WatcherTracker.Register` silently replaces an older connection, so the server won't catch a double recorder and the claim has to hold on the machine.

The claim is a new file. It can't reuse the watcher pid file, because `KillWatcher` signals and then force-kills whatever pid that file names, and that must never be the daemon's. The existing per-key spawn lock guards it.

**Stop and final drain.** `EnsureWatcherRunning` isn't the only path to the server. Session-end and subagent-stop hooks for at least seven vendors call `WatcherManager.KillWatcher` and then `InlineDrainAsync`, which reads the server's watermark and posts batches itself, with no claim and no stamps. An unstamped drain that wins would make the server discard the daemon's stamped copy. `CleanupCommand` and `Program.cs` stop watchers too. Both methods become claim-aware inside `WatcherManager`, so vendor call sites don't change:

| Who holds the source's claim | `KillWatcher` | `InlineDrainAsync` |
|---|---|---|
| A `kcap watch` process | Stops it, as today | Drains, as today |
| The daemon, heartbeat fresh | Signals nothing | Asks the daemon to finish that source, and waits within the hook's budget. If nothing listens on the holder's socket, the daemon died inside the grace and no later hook will find the claim stale, so this one takes the claim and drains, as for a stale claim, except from a hosted agent for its own hosted session |
| The daemon, heartbeat stale, terminal session | Signals nothing | Takes the claim and drains, as today |
| The daemon, heartbeat stale, hosted session, a hook from a hosted agent | Signals nothing | Does nothing. The daemon's catch-up finishes the source, so its rows keep their stamps |
| The daemon, heartbeat stale, hosted session, a hook from outside one | Signals nothing | Takes the claim and drains, as for a terminal session, so a session resumed by hand while the daemon is down is still recorded |
| Nobody | Nothing to stop | Drains, as today |

A hook asking for a recorder on a stale hosted claim, from a hosted agent, first asks its daemon to take the source. If the daemon declines, the hook takes the claim and spawns a watcher, unless the source failed the path check. If the daemon doesn't answer, or hasn't yet learned what its server supports, the hook does nothing and leaves the source to the catch-up.

Finishing a source runs the recorder's own final drain for that key and touches no other source, so a subagent stop leaves its parent running and a session end leaves other sessions running. If the hook's budget runs out, the daemon still finishes, because the hook only asked.

**`kcap disable`.** The command tells a person recording has stopped and their data is gone, so a daemon-held session has its own stop, with no final drain. The order is the marker in `DisabledSessions`, then the stop, then the server's deletion, because whatever would start a recorder reads the marker and a send landing behind the deletion can bring rows back. Nothing removes a person's marker: the server's deletion is permanent, and a session's end can't lift it, because a daemon or a person can resume that session under the same id. A hook's exclusion of an out-of-scope repository is a separate marker that only quiets hooks and comes off at the session's end.

The command finds a session's sources by their claims, a subagent's included, and asks every daemon on the machine over local control, behind a new capability, whichever profile it serves: only a daemon can say a live agent's output goes on, and neither a hosted session on a watcher nor an ACP session has a claim. A daemon with no socket has nothing running; one that takes the connection and doesn't answer may host the session. The daemon writes the marker where it reads it, since another config directory reads another. The command then releases the claims whatever came back, and stops each watcher, one whose pid file survives that still running.

The daemon cuts the session off at the transport, so a source in a backoff, in a final drain or stuck where no token reaches can't send when it comes round. A final drain under way stops rather than finishes: nothing is spooled, marked for import or handed to a title process. The daemon then waits, for a few seconds, for the server to answer what had already left, and takes no key's gate to do it. `InvokeAsync` completes locally on a cancel with the server still working, so a recorder that has ended says nothing about its last batch.

The marker is what reaches everything else, read just before each send. A recorder reads it beside its claim, so an older `kcap` that sends no frame still stops it. A watcher holds no claims and doesn't read it: the command stops one by signal. The daemon's title pushes, eval relays and every route to `EndAgentSession` read it, through the connection for the session end; a session an agent moved to counts, as its hooks handed it over. Both spool drains discard a disabled session and post nothing, the end sweeps queue no end for it, and the detached what's-done and Copilot finalizer send nothing for it.

What the command can't confirm it says, and it exits 1. Each run asks every daemon afresh and speaks only for what it found. The marker records one fact: a deletion ran while recording wasn't confirmed stopped. It is written after a deletion that found the session, never cleared, and every later run says that what landed behind it may still be on the server, where kcap can't tell or remove it. A daemon's answer that it held the session, made a stop or hosts its agent, with another server, says the data is there and wasn't deleted. That server goes into a file of its own beside the marker, also never cleared, because the run lets go of the claims and the daemon answers for none of it next time; every later run names it. Each fact is written before the line that reports the deletion, and a run that can't write one says so. A live agent's output and tools aren't recording and go on until the agent ends, whichever runtime carries it: a PTY's terminal mirror, or an ACP agent's transcript through its forwarder. So do its status and heartbeat, which carry no session content. The command says when an agent is live.

**Older binaries.** A hook binary from before this work doesn't know claims, so it can start a watcher or drain beside the daemon. The server's watermark and event ids keep the data intact. The cost is a row with no stamp, which falls back to the FIFO matcher. `kcap import` stays a manual repair and doesn't take claims.

**Daemon restarts.** The daemon's claim carries a heartbeat, as a watcher's does. A hook spawns a watcher for a daemon-claimed session only once that heartbeat has been stale for longer than a normal restart takes, so a daemon update doesn't push busy terminal sessions onto watchers. After a restart the daemon resumes each session in its persisted list from the line `WatcherConnect` returns, skipping any session a live watcher has claimed.

**Outages.** A longer outage moves a terminal session to a watcher, and it stays there until it ends. There is no takeover: steering doesn't need the daemon to be the recorder, and a line-position handoff between two recorders isn't worth its risk for a case this rare. An idle session with no hooks firing has nothing new to record, so its gap closes on restart.

**Hub.** The daemon uses the same hub (`/hubs/sessions`) and the same transcript methods, over a connection of its own. `WatcherConnect` takes a connection out of the UI topics, and the daemon's main connection has joined its owner's topic, so recording on that connection would silently cut it off. `StopWatcher` is pushed to a connection with no session in its payload, which works only while a connection carries one session. A second argument won't do, because a SignalR client drops an invocation whose argument count doesn't match its handler, so an older watcher would never stop. The server pushes `StopSource(key, reason)` beside `StopWatcher` instead. An older watcher has no handler for it and ignores it, and the daemon handles only `StopSource`. A daemon needs a server that has these before it records. It finds out by calling `WatcherDetach` for a key it doesn't hold, which does nothing on a server that has it and fails as an unknown method on one that doesn't. A server version won't do, because beta, dev and self-hosted servers don't send one. The server serialises transcript batches per source rather than per connection, so one stuck batch can't hold up the others, and the daemon bounds each send.

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
4. When the mod has nothing outstanding and no turn is running, it asks the daemon for the next dispatch. The daemon asks the server to confirm the handover, and only then passes it to the mod. The dispatch is now With the mod. A plugin prompt can't run before idle anyway, so waiting costs nothing, and a message still Held when someone runs `/clear`, resumes or exits mid-turn is rejected rather than carried into another conversation.
5. The mod keeps the dispatch in `$.store`, the only place that survives both a module reload and a session change, and submits it from a timer with `asUser: true`. That store is one file shared by every Claude process, so each process keys its entries with its process key, and removes keys left by processes that have gone.
6. The mod's `session.append` hook sees a `user` row with its own plugin origin and takes the uuid. The row's text must equal the dispatch's text.
7. The mod reports the link and the daemon forwards it to the server. The dispatch is now Delivered, and the mod can ask for the next one.
8. The server attaches the dispatch to the event whose id is that uuid. It finds the dispatch by id alone, because the row can land in a later session than the one the dispatch was sent to, and the link can arrive before the replacement agent attaches.

**Link ordering.** The row comes through the recorder and the link through the daemon, so either can reach the server first. The server must accept both orders. This path doesn't depend on who records the session, so a session on the fallback watcher can still be steered.

**Dispatch states.** The server owns the state. A dispatch is never silently lost, and it's never delivered twice.

| State | Can it still reach the agent? | Sender sees |
|---|---|---|
| Held: in the daemon's memory | Only after a confirmed handover | Waiting for the turn to end |
| With the mod | Yes | Waiting for the turn to end |
| Delivered: row linked | It has | Delivered |
| Rejected | No | Not delivered, send again |
| Unconfirmed | Possibly | Delivery unknown, check the session before sending again |

Only a dispatch that cannot land is Rejected, and only Rejected can tell the sender to send again.

| From | To | When |
|---|---|---|
| Held | With the mod | The server confirms the handover. It refuses for a dispatch it has already rejected, and the daemon drops that dispatch |
| Held | Rejected | The daemon's connection drops, the attachment lapses or ends, or the process has an Unconfirmed dispatch. The server does this itself, so a hard-killed daemon needs to report nothing |
| With the mod | Delivered | The link arrives |
| With the mod | Rejected | The submit returned `drop` or refused the text. Nothing else can prove the prompt won't land |
| With the mod | Unconfirmed | The session goes idle and no matching row appears within a grace period, or the session ends, or the attachment lapses, or the daemon stays away past a grace period |
| Unconfirmed | Delivered | A late link arrives. The sender has been told to check, so this corrects the record |

The mod holds one dispatch at a time, and no handover is confirmed while one is With the mod or Unconfirmed, so a plugin-origin row belongs to at most one dispatch. This barrier belongs to the Claude process, not the session id, so it holds across `/clear` and resume. An Unconfirmed dispatch stops steering for that process, and new messages are rejected with that reason until a late link arrives or the process exits. A row that matches nothing outstanding is reported as unmatched and stays unattributed.

The daemon drops its Held dispatches when its connection drops. It can't confirm a handover while disconnected, so nothing it's holding can reach the mod after the server has rejected it.

**Idle.** A turn completing doesn't prove a queued prompt will run, because an open dialog holds the queue with no signal. A dispatch can therefore go Unconfirmed while a dialog stays open, and a late link corrects it when the dialog closes. The grace period is a false alarm the design can absorb, and it stops steering only for as long as the dialog stays open.

**Stopped sessions.** After Esc during a turn, Claude refuses every plugin prompt until someone acts at the terminal, by typing a prompt or running `/clear`. Sending again would fail the same way, so the sender isn't told to. The mod knows this state from a turn that completed as aborted with no input since, and it never parses the engine's reason text. It reports the drop with that reason, and the sender sees "Not delivered, the session was stopped at the terminal". The dispatch is Rejected like any other drop, and the next one is handed over as normal.

**Commands.** A message that starts with `/`, after any leading whitespace, can't be delivered, because `$.prompt.submit` refuses it. For an attached agent the server refuses such a message before it reserves a dispatch, so it never reaches a mod. The sender sees "Not delivered, commands can only be run at the terminal". Hosted agents are unaffected.

**Session changes.** An attached agent belongs to one session id. `/clear` and resume end that session while the process carries on under a new id, and no `session.start` fires. The mod sees the end on `session.end` and the new id on `classic.SessionStart`. Held dispatches are rejected, because they were written for the old conversation. A dispatch With the mod goes Unconfirmed, because it's expected to land in the new conversation: in the proof it always did.

The mod then attaches again as a new agent under the new id, naming the agent it replaces so the server carries the barrier across. With nothing outstanding, steering carries straight on. With an Unconfirmed dispatch, the new agent takes no input until a late link arrives or the process exits.

**One process per session.** A second Claude process can resume a live session and keep its id, and neither mod is told. So an attachment belongs to a process as well as a session, and `/attach` names the process by a process key of its pid and start time, because a pid can be reused. The same process attaching again after a module reload or a daemon restart is accepted. A different process is refused while the first attachment is alive, and its mod stays inert and keeps trying until the first has gone.

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
   - **Result:** passed on macOS on 2026-11-06, as recorded under "What the spike proved". `socketPath` wasn't run on Windows or Linux. Part 3 isn't offered on Windows until it has been checked there, and the end-to-end run in step 6 covers Linux.
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
- Launch prompts are stamped.
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
- The mod takes a dispatch only when no turn is running.
- After Esc, a dispatch is Rejected with its own reason, and the sender isn't told to send again.
- A message that starts with `/` is refused before a dispatch is reserved.
- An orderly exit leaves a dispatch Unconfirmed, even though the proof suggests it can't land.
- A second process on a live session is refused, and its mod keeps trying until the first has gone.
- Part 3 isn't offered on Windows until `socketPath` has been checked there.
- Cursor and Antigravity stay on the fallback watcher, because they read SQLite and its native bundle must never reach the AOT daemon.
- `kcap disable` on a session the daemon holds stops recording at once with no final drain, because the command tells the person recording has stopped and their data is gone.
- A line can carry a local marker, which keeps it away from the FIFO matcher.
- The input log persists a hash of each submission rather than its text.
- Claims are a new file, separate from the watcher pid file.
- Older hook binaries can record beside the daemon, at the cost of an unstamped row.
