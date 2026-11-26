# Notification bell in the desktop app

## Goal

The desktop app shows the same notification bell as the web UI: the same inbox, the same actions (mark read, dismiss, mark all read, delete all, answer, reply) and the same settings (per-type in-app and Slack switches, Slack delivery state, pass controls). It sits in the session rail footer, left of Help, and updates live.

Mockup: https://kurrent.kcap.ai/pages/1828de2d8ef1431f9a96dd0e859fcce6

## Principle: one implementation, two front doors

The web UI (Blazor Server) keeps calling the notification services in-process; it does not move onto HTTP. The desktop app uses the public HTTP API and the hub. Consistency comes from both front doors being thin over the same server code:

- Every rule (what is external, what the Slack delivery state is, what an answer does) is decided once in `Capacitor.Server.Services` and read by both projections. Neither projection derives anything of its own.
- The public contract never references the web contract (repo rule). Parity is pinned against the source instead: a test projects a fully populated `RenderedNotification` publicly and fails if any field it carries is lost.
- One signal (`NotificationSignal.InboxChanged`) feeds both the web's UI topic and a new hub push.

## Part 1: kcap-server

All additive; no existing wire name or hub method changes.

1. **Item shape.** `NotificationItem` gains `LinkExternal` (bool), `Buttons` (list of `NotificationButton(Answer, Label, Style)`, style `default`/`primary`/`danger`) and `ReplyLabel`. `NotificationRun` gains `External`. "External" is computed by one server-side helper (resolved link that is not in-app) used by both projections.
2. **Answer route.** `POST /api/notifications/{logicalId}/answer` with `NotificationAnswerRequest(Answer, Text?)`, returning `NotificationAnswerResponse(Accepted, Message)`. It calls `NotificationAnswers.AnswerAsync`, the same service the web bell calls. A refusal is `200` with `Accepted: false` and the service's message, as on the web.
3. **Unread count.** `GET /api/notifications/unread-count` returning `NotificationUnreadCountResponse(Count)`, backed by the existing single `count(*)` query, so a badge refresh never pays for the full inbox.
4. **Slack delivery state.** `SlackPrefs` gains `Delivery` (`connected` / `needs_reconnect` / `not_connected`). The derivation moves from the web projection into a server-side method both projections call.
5. **Live push.** A per-user hub lane, modelled on `PublicWelcomeLane`: `Topic.Mine("notifications")`, `Admission.Own`, routed from `InboxChanged`, pushing `NotificationsChanged` with no arguments, collapsed to one frame per second per user. Joined in `HubConnection`.
6. **Feature gate.** `UserSurfacesResponse` gains `Notifications` (the `Features:Notifications` flag), so a client shows the bell exactly where the web UI does.
7. OpenAPI expected files, wire-contract tests, projection parity test, hub transport test.

## Part 2: kcap-cli desktop app

Named "inbox" in code to stay clear of the existing OS-notification services (`Services/Notifications/*`).

1. **Wire.** DTOs in `Capacitor.Remote.Models` with `[JsonSerializable]` entries, routes in `ApiRoutes`, `HubBroadcasts.NotificationsChanged`, a `NotificationsChanged` observable on `IServerLane` (bound in `ServerConnectionService`, listed in `EveryServerPushBindsToAHandler`).
2. **`InboxApi`.** On the `NextWorkApi` template over the shared `ICapacitorHttpClient`: inbox, unread count, read, dismiss, read-all, dismiss-all, answer, prefs get/put, pass controls, mute/unmute, agent opt-out. Outcomes are values; 404/403/405 mean "unavailable".
3. **`InboxBellViewModel`.** Visible only when signed in, `surfaces.Notifications` is true and the server has the routes. Badge from unread-count; the full list loads only while the panel is open. A push or reconnect triggers one coalesced re-read (single-flight, pushes filtered before `ObserveOn`). Dismissed rows stay hidden until a page no longer carries them, as in the web bell. Delete all confirms first.
4. **Views.** A `kcapChip` bell (Lucide `Bell`, 16 px) in a new footer column left of Help, with a numeric badge (99+) and the count in its accessible name. The panel is a `kcapPanel` flyout opening upward, 460 px wide, height capped at 70% of the window, with the web card anatomy. Settings open in a window, as Settings and Feedback do: Slack delivery, the type table (In-app / Slack, "In-app only", retired note) and Passes.
5. **Links.** Relative in-app links resolve against the server URL; all links open in the browser through `LinkPolicy`. "Open Chat accounts" opens the web app.
6. **Older servers.** Missing routes or a missing hub method hide the bell; nothing breaks.
7. Tests: `InboxApi` against WireMock, view-model behaviour with a fake API, a headless smoke test for open, mark-read and settings.

## Order and testing

Server first, then desktop. The server change is additive and safe to deploy on its own. Once it reaches kurrent.kcap.ai, the desktop branch can be built and run from source against the real server, with real notifications, before its PR merges. Until then, the desktop branch can run against a local server started from the server branch (`KCAP_URL`).

## Out of scope

OS-level desktop notifications for inbox items, a Dock badge for the inbox count, and opening in-app links inside the desktop app instead of the browser.
