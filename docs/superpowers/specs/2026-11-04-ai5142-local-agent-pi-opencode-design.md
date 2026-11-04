# Pi and OpenCode as local-agent harnesses

AI-5142 (Pi), AI-5143 (OpenCode), under AI-4515

## Intent

A local-edition server runs its background model calls (titles, imported-session outcomes, loose-end sweeps, next work, skill composition, work-item correlation, single-pass evals) on the user's own coding agent: the server sends `RunHeadlessPrompt` to the daemon, which runs the prompt through that harness's headless runner. Only Claude Code and Codex can be chosen. A user whose coding agent is Pi or OpenCode gets no background intelligence, or has to bring a provider key

Pi and OpenCode become choices with **the same capabilities Claude Code and Codex have there today**. Nothing in the server's local-agent path depends on which harness runs; parity comes from each new runner meeting the contract the Codex runner already meets

## Goals

- Pi and OpenCode are offered wherever Claude Code and Codex are: the desktop setup step, `kcap setup --local`, `kcap local intelligence`, and the admin AI settings' harness choice
- Each is offered only when the daemon resolves its CLI on this host, by the same rule as the others
- Every local-agent consumer works unchanged on either
- **Model:** the default is the harness's own default model, as the user already set it. A user can set any model the harness accepts

## Non-goals

- New features for the local agent. What is off for every local agent stays off: the policy judge, work-item and extended evals, and the tool-loop eval judge
- A turn or spend cap the harness does not have. Codex has neither; Pi and OpenCode are bounded by the deadline the same way
- Changing how Claude Code or Codex run

## Anti-goals

- A background prompt that becomes a recorded session, or that loads the user's tools, MCP servers, extensions, skills or project instructions
- A run that fails while reporting success. Pi exits 0 on failure; OpenCode retries a rate limit silently. Either one misread is a silent outage

## The contract each runner meets

The daemon's `IHeadlessHarness` returns a `ClaudeCliOutcome` that `HeadlessPromptResponses.From` maps to the server's answer. Each new runner sits in `Capacitor.Cli.Core/Harness/<Vendor>/` beside `CodexCliRunner` and returns the same records

| Part | Pi | OpenCode |
|---|---|---|
| Invocation | `pi -p --mode json` | `opencode run --format json --pure` |
| System prompt | `--system-prompt`, which replaces Pi's own | A delimited preamble of the prompt, as Codex's: OpenCode has no replacement flag |
| JSON schema | Appended to the prompt as an instruction; the reply must parse as a JSON object or array, re-serialised as the result; anything else is `OutputUnparseable`. Same check as Codex | Same |
| Model | `--model <value>` when not blank | `-m <value>` when not blank |
| Prompt delivery | Stdin, which `pi -p` reads when no prompt argument is given (measured) | Stdin when `opencode run` accepts it there, else argv; the builder confirms which against the binary. Argv is capped near 32K on Windows, so a long eval prompt on argv fails there |
| Working directory | A fresh empty temporary directory, removed afterwards | Same |
| Usage | The final `agent_end` event's assistant messages: tokens and cost | The `step_finish` events: tokens and cost, summed |
| Outcome | `stopReason` and `errorMessage` of the final assistant message, never the exit code | Its `error` event, its exit code, and its log on stderr |
| Deadline | Killed at it, the directory removed, usage reported so far kept | Same |

### Lockdown

- **Pi:** `--no-tools --no-extensions --no-skills --no-prompt-templates --no-context-files --no-session --offline`
- **OpenCode:** `--pure` (no external plugins, which is where kcap's own recording plugin lives); environment `OPENCODE_DISABLE_PROJECT_CONFIG`, `OPENCODE_DISABLE_CLAUDE_CODE`, `OPENCODE_DISABLE_EXTERNAL_SKILLS` and `OPENCODE_DISABLE_AUTOUPDATE` set; `OPENCODE_CONFIG_CONTENT` carrying every tool off, every permission denied, and **every MCP server in the user's OpenCode config set to `enabled: false` by name**
- **Both:** `KCAP_SKIP=1`, so no kcap hook or watcher records the run

**Measured:** with the user's config holding 10 kcap MCP servers, a plain `opencode run --pure` started all 10; with each named `enabled: false` in `OPENCODE_CONFIG_CONTENT`, it started none. OpenCode merges its configs, so there is no "no MCP servers" switch; the runner must read the names. It reads them from the config files OpenCode loads (`opencode.json`, `opencode.jsonc` and `config.json` in its config directory, resolved through `OpenCodePaths`, plus the file `OPENCODE_CONFIG` names), parsing JSONC (comments, trailing commas). A file that cannot be read or parsed contributes no names and is logged; the run still goes ahead, since OpenCode would refuse that same file itself

**Measured:** without the lockdown, OpenCode ran `cat` and read a file in its working directory. With it, no tool ran. Whether the whole lockdown still answers a plain prompt is confirmed against a paid model at build time: the free OpenCode models hit their usage limit mid-check, which is how the next point was found

### Failure classification

`ClaudeCliFailure` kinds, so the server's existing handling applies unchanged:

| Kind | Pi | OpenCode |
|---|---|---|
| `NotSignedIn` | HTTP 401 or 403 in `errorMessage`, or no model/provider available | `statusCode` 401 or 403 in the error event |
| `SpendBudget` | 429, "credit balance", "quota", "usage limit" | The same in the error event, **or a `stream error` log line carrying them** |
| `Timeout` | The deadline | The deadline with no limit error logged |
| `OutputUnparseable` | `stopReason: stop` with an empty reply, or a schema reply that is not JSON | Same |
| `ProcessFailure` | Anything else | Anything else |

**Measured:** when the provider reports a usage limit, OpenCode logs a `stream error` and retries without emitting any JSON event until it is killed. Reading only stdout would report that run as a deadline. The runner passes `--print-logs --log-level ERROR` and watches stderr; on a limit error it kills the run at once and answers `SpendBudget`, rather than waiting out the deadline

## Model routing (server)

`LocalAgentModels.Route` gains a branch for `pi` and `opencode`: a tier name (`haiku`, `sonnet`, `opus`, case-insensitive) becomes an empty model, which the runner omits, so the harness runs its own default. Any other value passes through unchanged. This is Codex's shape without the reasoning effort. Every per-feature default is a tier name, so with no setting changed, Pi and OpenCode run the user's own default

The admin AI settings' help for the local-agent model says: for Pi and OpenCode, a tier name means the harness's own default; otherwise enter the model as that harness names it (for OpenCode, `provider/model`)

## Surfaces

**Server (kcap-server):** `LocalAgentHarnesses` gains `Pi = "pi"` and `OpenCode = "opencode"` in `All`, which is the valid-values list for `Evals:ServerRunner:LocalAgent:Harness`, the setup route's check and the availability check. `LocalAgentModels.Route` as above, and the help text

**CLI (kcap-cli):**
- `HeadlessHarnessProbe` lists both, resolved through `HarnessRegistry.LaunchCommand` like the others
- `IntelligenceAnswer.Harnesses` lists both
- The labels in `IntelligenceOption` and `IntelligenceQuestions` read "Pi" and "OpenCode"; the setup card for these two says "Runs on this machine through <name>'s own sign-in or key", since they usually run on a provider key the user configured in them
- `PiHeadlessHarness` and `OpenCodeHeadlessHarness` in the daemon, resolving the CLI from `DaemonConfig.PiPath` and `DaemonConfig.OpenCodePath`, registered beside the Claude and Codex ones, so `HeadlessVendors` advertises them

## Order and compatibility

The two halves are independent. A server that accepts `pi` before any daemon advertises it answers `no_daemon`, as for any unadvertised harness; a daemon advertising `pi` to an older server is never asked for it, because that server's setup and settings do not offer it. Either PR can land first. The CLI PR carries this document

## Testing

- **Unit:** argument building, prompt building, event parsing (usage, cost, outcome) and failure classification, from captured real output of each binary
- **Real binaries,** behind the same gate as the Codex runner's invocation tests: a plain answer, a schema answer, a refused non-JSON schema reply, a deadline kill with the directory gone, and no tool run. For OpenCode, also that no configured MCP server starts. Spend is kept to a handful of runs on the cheapest model
- **Server:** `LocalAgentModels.Route` for both harnesses, and the settings registry accepting the new values

## Left to the builder

- Whether Pi reads its prompt from stdin, confirmed against the binary
- How OpenCode's tokens split into input, output and cache in `step_finish`, mapped onto `ClaudeCliUsage`
- Any shared helper between the two new runners and `CodexCliRunner`, if one reads better than three copies; the first two may share an event-stream reader
