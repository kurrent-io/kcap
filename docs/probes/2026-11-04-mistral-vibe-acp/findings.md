# Mistral Vibe ACP hosting probe — 2026-11-04

**Subject:** `vibe-acp 3.0.1` (Mistral Vibe), Windows 11 x64, signed in through Mistral AI Studio (no
`MISTRAL_API_KEY`), run with no arguments in a fresh git repository
**Drivers:** `probe.py <cwd> [prompt] [model]` speaks ACP to `vibe-acp` over stdio and answers every permission
request with `allow_once`. `watch_hooks.py <seconds>` polls the process table and prints the ancestor chain of
every `kcap.exe` it sees. Each run spends one short model turn

## 1. Surface

`initialize` advertises `loadSession`, `promptCapabilities {image, embeddedContext}` and
`sessionCapabilities {list, fork, close}`, with one auth method, `browser-auth`. A signed-in Vibe needs no
`authenticate` call. `session/new` answers `{sessionId, modes, configOptions, _meta.workspace_trust}`.
`configOptions` holds `mode`, `model` (`category: "model"`, a `select`) and `thinking`. The session starts in
`accept-edits` mode

## 2. Turns and usage

A prompt streams `agent_thought_chunk`, `agent_message_chunk`, `tool_call` and `tool_call_update`, and ends
`end_turn` with `usage {inputTokens, outputTokens, cachedReadTokens}`. `usage_update` carries a running `cost`
in USD

## 3. Permissions

- In `accept-edits` mode a file write and `git status` ran without any `session/request_permission`
- `curl … https://example.com` raised one. Its `toolCall` is `{toolCallId}` alone, with no `title`, `kind` or
  `rawInput`; those arrived earlier in that call's `tool_call` update (`title: "bash: curl …"`,
  `kind: "execute"`, `rawInput: {command}`). The options are `allow_once`, `allow_always` (session),
  `allow_always_permanent` (`allow_always`) and `reject_once`

So the daemon fills a request's missing fields from the call's announcement (`AcpAnnouncedToolCalls`).
Without that the approval card reads "Tool call", and a preset or policy rule has no kind or command to judge

## 4. Hooks run inside `vibe-acp`

kcap's `~/.vibe/hooks.toml` hooks fire in ACP sessions too, and their `session_id` is the ACP `sessionId`, so
a hosted session would be recorded twice. The process chain from the hook up was
`kcap.exe ← node.exe ← sh.exe ← bash.exe ← python.exe ← python.exe ← vibe-acp.exe`: the npm launcher, Git Bash
and Vibe's Python launcher sit between them, so a parent-process check like Grok's cannot find the daemon's
child. `kcap hook --mistral-vibe` instead asks the daemon which session the hosting agent is bound to and stands
down on a match

Adding a hook of our own to `~/.vibe/hooks.toml` disabled every hook, kcap's included, until the file was
restored

## 5. End to end through a daemon

A daemon built from this branch advertised `mistral-vibe`. `start_agent` launched it in an owned worktree; the
handshake resolved `mistral-medium-3.5`, the write landed, and the `curl` approval card read
"Allow bash: curl -s -o NUL -w … https://example.com?". Allowing it once finished the turn with the reply `200`
and the session's cost on the agent page. The hook ran in the hosted child and wrote no copy of that session

## Not measured

`session/load` across a killed owner, `session/new.mcpServers`, and a model switch through
`session/set_config_option` (only one model was configured). Reconnect and restart resume stay off
