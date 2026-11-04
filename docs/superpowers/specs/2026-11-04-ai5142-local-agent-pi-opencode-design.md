# Pi and OpenCode as local-agent harnesses

AI-5142 (Pi), AI-5143 (OpenCode), under AI-4515

## Intent

A local-edition server runs its background model calls (titles, imported-session outcomes, loose-end sweeps, next work, skill composition, work-item correlation, single-pass evals) on the user's own coding agent: the server sends `RunHeadlessPrompt` to the daemon, which runs the prompt through that harness's headless runner. Only Claude Code and Codex can be chosen. A user whose coding agent is Pi or OpenCode gets no background intelligence, or has to bring a provider key

Pi and OpenCode become choices with **the same capabilities Claude Code and Codex have there today**. Nothing in the server's local-agent path depends on which harness runs; parity comes from each new runner meeting the contract the Codex runner already meets

## Goals

- Pi and OpenCode are offered wherever Claude Code and Codex are: the desktop setup step, `kcap setup --local`, `kcap local intelligence`, and the admin AI settings' harness choice
- Each is offered only when the daemon resolves its CLI on this host, by the same rule as the others
- Every local-agent consumer works unchanged on either, long eval prompts included, on every platform the daemon runs on
- **Model:** the default is the harness's own default model, as the user already set it. A user can set any model the harness accepts

## Non-goals

- New features for the local agent. What is off for every local agent stays off: the policy judge, work-item and extended evals, and the tool-loop eval judge
- A turn or spend cap the harness does not have. Codex has neither; Pi and OpenCode are bounded by the deadline the same way
- Changing how Claude Code or Codex run
- Checking at setup that a sign-in or key works. Claude Code and Codex are offered on their CLI resolving, and so are these two; a run without credentials answers `not_signed_in`, which the local-agent status already surfaces

## Anti-goals

- A background prompt that becomes a recorded session, at the time or later through `kcap import`, or that appears in the harness's own session list
- A background prompt that loads the user's tools, MCP servers, extensions, skills, agents, or global or project instructions. This binds every source the user or a repository controls. Configuration an administrator imposes on every run of the harness on the machine, which the harness does not let a user override, is outside it (see Residuals)
- A run that fails while reporting success. Pi exits 0 on failure; OpenCode retries a rate limit silently. Either one misread is a silent outage
- A schema-bound caller receiving valid JSON of the wrong shape as a successful answer

## The contract each runner meets

The daemon's `IHeadlessHarness` returns a `ClaudeCliOutcome` that `HeadlessPromptResponses.From` maps to the server's answer. Each new runner sits in `Capacitor.Cli.Core/Harness/<Vendor>/` beside `CodexCliRunner` and returns the same records

| Part | Pi | OpenCode |
|---|---|---|
| Invocation | `pi -p --mode json` | `opencode run --format json --pure` |
| System prompt | Always `--system-prompt <file>` and `--append-system-prompt ""`, the file written in the run's directory (UTF-8, no BOM), never the text on argv: on Windows `pi` is the npm shim `pi.cmd`, run through `cmd.exe`, which ends an argument at its first newline (dropping the flags after it), expands `%VAR%` and caps the line at 8191 characters. Pi reads a `--system-prompt` value that names an existing file as that file's content, as `PiRpcHostedAgentRuntimeFactory` passes its reviewer's. With no system prompt in the request, a fixed neutral one ("Answer the request directly.") | A delimited preamble of the prompt, as Codex's: OpenCode has no replacement flag. Global instructions cannot reach it (see Lockdown) |
| JSON schema | Appended to the prompt as an instruction, then the reply validated against the schema (below) | Same |
| Model | `--model <value>` when not blank | `-m <value>` when not blank |
| Prompt delivery | Stdin, which `pi -p` reads when no prompt argument is given (measured) | A file in the run's directory, attached with `--file`, and a short fixed message on argv pointing at it. Never the prompt itself on argv, which is capped near 32K on Windows |
| Working directory | A fresh empty temporary directory, removed afterwards; the system prompt file sits beside it, not in it | Same, holding only the prompt file |
| Usage | The final `agent_end` event's assistant messages: tokens and cost | The `step_finish` events: tokens and cost, summed |
| Outcome | `stopReason` and `errorMessage` of the final assistant message, since Pi exits 0 when the provider refuses; a nonzero exit after a clean stop is still a `ProcessFailure` | Its `error` event, its exit code, and its log on stderr. A reply counts only when a `step_finish` follows its text: a stream cut off mid-step exits 0 with part of it |
| Deadline | Killed at it, every directory removed, usage reported so far kept. The daemon gives the runner a timeout 2 seconds short of the time left to the deadline (never below zero), so the runner's own timeout, which keeps the usage, fires before the daemon's outer deadline, which answers with none. A descendant still holding the output open after the harness exits is killed there too, and the run answers `Timeout`. However a run ends, every descendant still alive is killed and waited for before its directory is removed. All of a run's cleanup waits share one 2-second budget from its first kill, the same 2 seconds, so a Timeout answer arrives by the deadline and cannot hold a daemon's headless slot; a process that outlives the budget is left killed but unconfirmed, its directory removed best effort, and the next daemon start's sweep is the backstop; descendants are listed while the harness runs, since its exit reparents them out of its tree | Same |

### Lockdown

**Pi:** `--no-approve --no-tools --no-extensions --no-skills --no-prompt-templates --no-themes --no-context-files --no-session --offline`, plus the two system-prompt flags above, the containment set `PiRpcHostedAgentRuntimeFactory` gives its reviewer minus its extension and tool allowlist. `--offline` is what stops Pi installing the operator's configured npm packages at startup (probe §8). A repository probe (`docs/probes/2026-09-21-pi-reviewer/findings.md` §5) measured that the operator's `SYSTEM.md` and `APPEND_SYSTEM.md` survive `--no-context-files`: only an explicit `--system-prompt` displaces the first and an empty `--append-system-prompt` the second, which is why both are always passed, the same pair `PiRpcHostedAgentRuntimeFactory` passes its reviewer

**Pi's executable configuration, kept:** Pi's agent directory may name shell commands that Pi runs on the user's behalf: an `npmCommand`, spawned once as `root -g` even under `--offline` (probe §8), and `!`-prefixed values in `auth.json` and `models.json`, run on the first model request to resolve a key or header (probe §12). These are how the user's Pi obtains its credentials (a password-manager lookup, say), and every interactive Pi run executes them; suppressing them would sign the local agent out for exactly the users who configured them. The local agent runs them as Pi does, from the user's own agent directory, and nothing else of that directory reaches the run: its extensions, skills, prompts, themes, `SYSTEM.md` and `APPEND_SYSTEM.md` are all displaced by the flags above. A test plants an `npmCommand` that leaves a marker and asserts it ran only as `root -g`, never `install`

**OpenCode** runs with a private configuration and a private data store, built per run and removed afterwards. This is the shape of the existing unattended OpenCode reviewer (`OpenCodeReviewerConfigDir`), extended to data:

- `XDG_CONFIG_HOME` and `OPENCODE_CONFIG_DIR` both set to fresh empty directories, verified empty. **`OPENCODE_CONFIG_DIR` alone does not do it:** it adds a config layer and the global `$XDG_CONFIG_HOME/opencode` still loads (measured with `opencode debug config --pure`: with only `OPENCODE_CONFIG_DIR` empty, all 10 of the user's MCP servers resolved; with a private `XDG_CONFIG_HOME` too, none). Together they remove the global config, its MCP servers, plugins, agents, `AGENTS.md` and `instructions` as one source, with no list of names to get wrong. `OPENCODE_CONFIG` removed from the environment, since it names an additional merged file
- `OPENCODE_CONFIG_CONTENT` carrying the whole configuration the run gets: every tool off, every permission denied, and two keys taken from the user's effective configuration so the default stays theirs: `model`, and `provider` (custom providers and their options). Nothing else is copied. An inherited `OPENCODE_CONFIG_CONTENT` is overwritten
- `OPENCODE_PERMISSION` set to the same deny-all, overwriting an inherited value: it overrides config permissions (measured, `docs/probes/2026-08-07-opencode-acp/findings.md` §6), so an operator's `{"*":"allow"}` would otherwise reopen the tools. Its shape follows `OpenCodeReviewerPermissions`: `*` denied plus each forbidden tool named
- `HOME` (and `USERPROFILE` on Windows) and `OPENCODE_TEST_HOME` all set to one fresh empty directory. OpenCode always loads `opencode.json` and `opencode.jsonc` from `.opencode` under its home, which is `OPENCODE_TEST_HOME` when set and the OS home otherwise, along with that directory's agents and commands; no variable or flag turns it off (measured with `opencode debug config --pure`: an MCP server and `instructions` planted in `~/.opencode/opencode.json` resolved with every other variable here set, and neither did with a private `OPENCODE_TEST_HOME`). `~/.opencode` exists for everyone who installed OpenCode with its install script
- `XDG_CACHE_HOME` pinned to the user's own cache, the inherited `XDG_CACHE_HOME`, else `.cache` under the inherited home (`USERPROFILE` on Windows, `HOME` elsewhere), where OpenCode would put it. Otherwise the private home would move the cache, and with models fetch off the cache holds the model list
- `XDG_DATA_HOME` set to a fresh empty directory, so the run's session database is written there and never to the user's `opencode.db`, where `kcap import --opencode` and OpenCode's own session list would find it (measured: with a private `XDG_DATA_HOME` the run's database appears only there). Credentials live in the data directory, so the user's `auth.json` is passed in `OPENCODE_AUTH_CONTENT` (measured: a key supplied that way reaches the provider) rather than copied to disk
- `OPENCODE_DISABLE_PROJECT_CONFIG`, `OPENCODE_DISABLE_CLAUDE_CODE`, `OPENCODE_DISABLE_EXTERNAL_SKILLS`, `OPENCODE_DISABLE_AUTOUPDATE` and `OPENCODE_DISABLE_MODELS_FETCH` set
- `--pure`, so no external plugin loads even if one were configured

**Which files the two keys come from,** before the variables naming them are replaced, in OpenCode's own precedence, later winning (measured with `opencode debug config --pure`, one layer per source): the XDG global config directory (`$XDG_CONFIG_HOME/opencode`, else `~/.config/opencode`), its `config.json`, `opencode.json` and `opencode.jsonc` in that order; then the file the inherited `OPENCODE_CONFIG` names; then `opencode.json` and `opencode.jsonc` in `.opencode` under the home OpenCode resolves (the inherited `OPENCODE_TEST_HOME`, else the user's home); then `opencode.json` and `opencode.jsonc` in the directory the inherited `OPENCODE_CONFIG_DIR` names, which OpenCode loads in addition to the XDG directory, not instead of it; then the inherited `OPENCODE_CONFIG_CONTENT`. Neither `.opencode` nor `OPENCODE_CONFIG_DIR` contributes a `config.json`. Project config is skipped, as the run disables it. `model` is replaced by a later layer; `provider` is merged deeply, as OpenCode merges it: objects key by key at every level, any other value replaced by the later one. Files are parsed as JSONC (comments, trailing commas). Reading fails closed: a layer that is present but cannot be read or parsed stops the run before it starts, answering `process_failure` naming it, since OpenCode itself would refuse it. No layer at all means OpenCode's own default model

**Both:** `KCAP_SKIP=1`, so no kcap hook or watcher records the run. Provider keys in the daemon's environment are kept, unlike Codex, which strips `OPENAI_API_KEY` because it would override a ChatGPT login: for Pi and OpenCode a provider key usually is the sign-in

**Measured:** without any lockdown, OpenCode ran `cat` and read a file in its working directory. With a plain `opencode run --pure` it started every MCP server in the user's global config (10 kcap servers on the test machine). Whether the full lockdown still answers a plain prompt is confirmed against a paid model at build time. If turning every tool off stops OpenCode answering, the tools stay on and the deny-all permissions are the boundary, as they are for the reviewer; the permissions are never loosened

### Residuals

- **Administrator-imposed OpenCode configuration.** OpenCode's managed config (a system directory, writable only by an administrator) outranks every other layer, and a remote organisation config can arrive through a `wellknown` sign-in. Neither is under `OPENCODE_CONFIG_DIR`, and OpenCode offers a user no way to override either; whatever MCP servers, instructions or permissions they set apply to this run as they do to every OpenCode run on the machine. kcap does not contain them, and the anti-goal is scoped to say so. The private data store and `KCAP_SKIP` still keep the run out of recorded sessions. When a managed config file is present, the runner logs that it applied
- **Pi's credential commands,** above
- **OpenCode's home.** Containing `~/.opencode` depends on `OPENCODE_TEST_HOME`, a variable OpenCode reads but does not document; a release that stops reading it reopens that layer, which the live test planting an MCP server there catches. A provider that reads its credentials from a file under the home (an AWS or Google Cloud profile, say) does not find it under the private home; a provider key in the environment or in `auth.json` is unaffected

**Per-run directories are owner-only, verified, on every platform.** The prompt file and OpenCode's session database hold private session material, and the daemon's state root can be moved (`KCAP_DAEMONS_DIR`) and is only best-effort owner-only. So the runs get their own root, `headless-runs` under the daemon's state directory, created and verified owner-only with `OwnerOnlyDirectory.CreateVerified` (Unix mode or Windows ACL read back) on every run, as `OpenCodeReviewerConfigDir` does its own root; a root that cannot be verified private refuses the run. Its parent chain is checked too, since another user able to write any ancestor could rename or replace the entry below it between its check and its use: on Unix every ancestor of the root, the state directory included, must be owned by the current user or by root and, unless it carries the sticky bit (as `/tmp` does), not writable by others, nor by a group the current user is not proven to be alone in. That proof, which lets a `umask 002` home on a user-private-group distribution pass, comes from the two account files only: `/etc/group` lists the directory's gid, its members name nobody but the current user, no other account in `/etc/passwd` has that gid as its primary group, and the current user is in it by one of the two. The directory must also carry no extended ACL, since with a POSIX ACL the mode's group bits show the mask and a named user can still write: `ls -ld` must show no `+`, and a failed `ls` or an unreadable mode field refuses, as does macOS's `@`, which `ls` prints in place of the `+` and so hides it. Only a group-writable ancestor is asked this, since macOS home directories carry an ACL by default. On macOS, whose ACLs are independent of the mode bits, every ancestor is also read with `ls -lde`, and the chain fails on an `allow` entry, inherited or not, that gives anyone but the current user, `root` or `wheel` one of `write`, `delete`, `delete_child`, `add_file`, `add_subdirectory`, `append`, `writesecurity` or `chown`; `deny` entries pass, and a failed `ls`, a line that cannot be read or an unknown right fails it. Linux needs no such read, because a POSIX ACL's mask shows in the group bits. An unreadable file, a gid not listed (an LDAP or other directory-service group), a line that includes a directory service, or a gid that cannot be read refuses the run; the sibling check on service units is unchanged; on Windows the check is for the rights that can replace the next entry down the path, granted to anyone but the current user, `SYSTEM` and `Administrators` (and `CREATOR OWNER`, which resolves to the entry's creator): `DELETE_CHILD` on an ancestor, `DELETE` on the entry below it, and `WRITE_DAC` or `WRITE_OWNER` on either, counting effective rights from inherited entries and the generic rights that include them (`GENERIC_ALL`, `GENERIC_WRITE` where it maps to these). Creating a sibling (`FILE_ADD_SUBDIRECTORY`, `FILE_ADD_FILE`), which ordinary users hold at a volume root, cannot rename or replace an existing entry and is allowed. The chain is walked to the volume root on both platforms, so a state directory moved with `KCAP_DAEMONS_DIR` under a shared parent fails it. A chain that fails refuses the run, whatever `DaemonRunner.PrepareStateDirectory` tolerated at startup. Each per-run directory, Pi's and OpenCode's working directory, OpenCode's config directory and its data directory, is created inside that root the same way and verified empty before anything is written into it or the harness starts. Any failure stops the run before it starts, answering `process_failure`. They are removed after the run, and the daemon's start sweeps any a crash left, as it does the reviewer's. `OPENCODE_AUTH_CONTENT` keeps credentials off disk on every platform

### Structured output

Neither harness constrains its output to a schema, so the runner checks the reply itself. The schema is appended to the prompt with an instruction to answer with one JSON value matching it; the reply (with a surrounding Markdown code fence stripped) must parse, and must validate against the schema. **Vocabulary.** Validated: `type` (one or a list), `properties`, `required`, `additionalProperties` (boolean or schema), `items`, `enum`, `const`, `anyOf`, `oneOf`, `allOf`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `minLength`, `maxLength`, `minItems`, `maxItems`, `pattern`, and `$ref`. `$defs` and `definitions` are definition containers: their entries are not applied to the instance, only reached through a `$ref`. A `$ref` must be local (`#`, `#/$defs/<name>`, `#/definitions/<name>`, JSON-pointer escaped); any other target fails closed. References resolve against the root schema, and validation stops at a nesting depth of 64, a reply that reaches it failing the check, which bounds a recursive schema. Annotations are ignored: `description`, `title`, `default`, `examples`, `format`, `$schema`, `$id`, `$comment`, `readOnly`, `writeOnly`, `deprecated`. **Any other keyword fails closed:** the run does not start and answers `process_failure` naming the keyword, so no constraint is ever silently unchecked. The server PR adds a test that every schema a local-agent consumer sends uses only this vocabulary, so the closed failure is a guard rather than a live path. A reply that fails parsing or validation is `OutputUnparseable`; a valid one is returned re-serialised, as Codex's is. The validator lives in `Capacitor.Cli.Core` and is AOT-safe (no reflection), since no JSON-schema library is in the CLI's dependencies

### Failure classification

`ClaudeCliFailure` kinds, so the server's existing handling applies unchanged:

| Kind | Pi | OpenCode |
|---|---|---|
| `NotSignedIn` | HTTP 401 or 403 in `errorMessage`, or no model or provider available | `statusCode` 401 or 403 in the error event |
| `SpendBudget` | 429, "credit balance", "quota", "usage limit" | The same in the error event, **or a `stream error` log line carrying them** |
| `Timeout` | The deadline | The deadline with no limit error logged |
| `OutputUnparseable` | `stopReason: stop` with an empty reply, or a reply that fails the schema check | Same |
| `ProcessFailure` | Anything else, including an unreadable global config | Same |

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
- `PiHeadlessHarness` and `OpenCodeHeadlessHarness` in the daemon, resolving the CLI from `DaemonConfig.PiPath` and `DaemonConfig.OpenCodePath`, registered beside the Claude and Codex ones, so `HeadlessVendors` advertises them. The OpenCode one creates and removes its per-run directories under the daemon's state directory, and the daemon's start sweeps any a crash left, as it does the reviewer's

## Order and compatibility

The two halves are independent. A server that accepts `pi` before any daemon advertises it answers `no_daemon`, as for any unadvertised harness; a daemon advertising `pi` to an older server is never asked for it, because that server's setup and settings do not offer it. Either PR can land first. The CLI PR carries this document

## Testing

- **Unit:** argument and environment building, prompt building, event parsing (usage, cost, outcome), failure classification and the schema validator, from captured real output of each binary
- **Real binaries,** behind the same gate as the Codex runner's invocation tests, on the cheapest model and a handful of runs:
  - Both: a plain answer; a schema answer; a valid-JSON reply of the wrong shape refused, including one that breaks only a `minimum` or `minLength`; a schema whose shape sits behind a local `$ref` into `$defs`, accepted and refused correctly; a schema with an unsupported keyword refused before any run; a deadline kill with every directory gone; a run refused before it starts when the `headless-runs` root is group- or world-writable, even though each per-run directory could be made owner-only, and again when the root is 0700 but the state directory above it is world-writable without the sticky bit, and on Windows when `KCAP_DAEMONS_DIR` puts a private state directory under a parent where every user holds delete-child; and, positively, a default install whose volume root grants `Users` create-folder still runs; no tool run, with a file planted in the working directory that must not appear in the reply
  - Pi: a planted `SYSTEM.md` and `APPEND_SYSTEM.md` reach no request; no session file is written to Pi's session directory; a planted `npmCommand` runs only as `root -g`
  - OpenCode: an inherited `OPENCODE_PERMISSION` of `{"*":"allow"}` and an inherited `OPENCODE_CONFIG` naming an MCP server change nothing; no configured MCP server starts; an MCP server and an `instructions` file planted in `~/.opencode/opencode.json` reach neither the run nor its resolved config, while `opencode debug config --pure` under the same environment with the user's own home resolves both; the run is absent from the user's `opencode.db` and from `kcap import --opencode` discovery; a long prompt (over 32K characters) is answered; the user's `model` and a custom `provider` are honoured from the global config directory and from a file named by `OPENCODE_CONFIG`, the later winning; a malformed layer refuses to start
- **Server:** `LocalAgentModels.Route` for both harnesses, the settings registry accepting the new values, and every schema a local-agent consumer sends using only the validator's vocabulary

## Left to the builder

- How OpenCode's tokens split into input, output and cache in `step_finish`, mapped onto `ClaudeCliUsage`
- The wording of the schema instruction and of the OpenCode pointer message
- Any shared helper between the two new runners and `CodexCliRunner`, and whether the OpenCode per-run directories share code with `OpenCodeReviewerConfigDir`
