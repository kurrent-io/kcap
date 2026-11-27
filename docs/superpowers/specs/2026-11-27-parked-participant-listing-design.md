# Parked flow participant in the session listing (#3135)

## Problem

Between review rounds the daemon parks a resumable Codex reviewer. The process stops and the agent leaves
the daemon registry. The session is not ended, so the Codex thread can be resumed. The server's listing
keeps the session `active` and says nothing about the park. With no daemon row, the app's only row comes
from the listing, and 30 s after the daemon row goes it is marked a terminal session.

## Server (kcap-server)

**Field.** `RepoSession` gains a body member `parked` (bool, JSON `parked`). It is a body member, not a
positional one, so an older client keeps its constructor. It is true when both of these hold:

- the session's `sessions.status` is active;
- `hosted_session_ledger` has a row for the session's id with `disposition = 'parked'`.

The ledger key and `sessions.session_id` are the same 32-hex id. `RepoSessionsSql.Data` reads the field
with a `LEFT JOIN hosted_session_ledger` on that primary key.

The field is not withheld below full access: like `status` and `stale`, it describes whether the session
is live, not what it contains. Every producer of a `RepoSession` row sets it, including the pinned-row
read if that is a separate path.

**Push.** The ledger writes publish nothing today. Publish `SessionSignal.Changed(id, Live: true)` in two
places, modelled on `CaptureAccountingPublication`:

- after `ParkAsync` returns `Parked` or `AlreadyParked`;
- after a claim moves a parked row back to `active`.

That reaches clients as `ActiveSessionChanged`. The app then re-reads the live listing within its 5 s
sample window instead of waiting for the next 60 s reconcile.

**Docs and pins.** Update the row-shape rows in `docs/API_REFERENCE.md` and regenerate the three
`public.expected.yaml` OpenAPI snapshots.

## App (kcap-cli)

- `RepoSessionDto` gains `Parked`. Absent means false, so an older server gives today's behaviour.
- `AgentRow.FromServer`: an active, parked listing row gets
  - `Status = AgentRow.ParkedStatus` ("Parked"), next to the existing synthetic `DaemonDownStatus`;
  - `IsTerminalSession = false`;
  - `IsStale = false`, because sitting parked for hours while the implementer works is normal.
- `SessionStatusDots.Present`: `"Parked"` maps to a new `AgentStatusKind.Parked`, with label "Parked",
  sentence "Parked between review rounds", and the fact "The flow resumes it for the next round". It
  ranks below Failed and DaemonDown, and above Stale.
- `AgentStatusPresentation`: Parked draws the muted settled mark. It is not a warning, not in flight, and
  shows no clock. Idle is a warning that means waiting on the user, which a parked reviewer is not.
- `RemoteSessionViewModel.SessionInfo`: a parked server row gets `ParkedNote`, "Parked between review
  rounds · the flow resumes it for the next round", instead of `TerminalNote`. The chat stays read-only
  (`ReadOnlyChatInput`).
- The JetBrains bridge (`SessionRowMapping`) carries `Status`, `IsStale` and `IsTerminalSession` from
  `AgentRow`, so it shows "Parked" and drops the terminal mark with no change of its own.

## States

| Listing status | `parked` | Daemon row | App shows |
|---|---|---|---|
| active | any | present | the daemon row (unchanged; the hosted row's status wins) |
| active | false | gone, under 30 s | hosted look during the hand-over (unchanged) |
| active | false | gone, over 30 s | terminal session (unchanged: a real terminal session) |
| active | true | gone | **Parked**: no terminal chip or note, not stale |
| ended | (always false) | any | ended (unchanged) |
| active | (field absent: older server) | gone, over 30 s | terminal session (today's behaviour) |

## Order of events

- **Park.** The server acks the park (ledger → `parked`, then the push) before the daemon stops the agent,
  so the re-read is normally in hand before the 30 s grace runs out. If both the push and the re-read are
  lost, the 60 s reconcile corrects the row, and up to 30 s of "terminal" can show in the meantime.
- **Resume.** The daemon launches the resumed agent first, and its registry row appears and takes over.
  The claim then moves the ledger to `active` and pushes, and the next read clears `parked`. There is no
  window with neither a daemon row nor `parked`.

## Limits

- When a flow ends while its reviewer is parked, the ledger leaves `parked` (closing, then closed) before
  the session's end lands, so the row can read as a terminal session in between. Both reviewers observed
  today did end. The server work checks what records that end and how long the gap is; if it is more
  than a moment, `parked` also covers a `closing` row that was parked.
- A reviewer that cannot resume (PTY or non-Codex) is reaped and its session ended, never parked, so it
  never reaches this state.
