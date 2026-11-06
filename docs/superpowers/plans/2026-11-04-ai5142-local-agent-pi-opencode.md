# Pi and OpenCode as local-agent harnesses: implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** The daemon runs a local-edition server's headless prompts on Pi and OpenCode, and setup, settings and the server offer both, with the capabilities Claude Code and Codex have

**Architecture:** Two new runners in `Capacitor.Cli.Core/Harness/{Pi,OpenCode}/` return `ClaudeCliOutcome` like `CodexCliRunner`, over shared pieces: a schema validator, a failure classifier and a private per-run directory root. Two `IHeadlessHarness` adapters register them in the daemon. The server adds the two tokens and routes tier names to the harness default

**Tech Stack:** .NET 10, AOT-published CLI (no reflection-based JSON), TUnit on Microsoft Testing Platform

**Spec:** `docs/superpowers/specs/2026-11-04-ai5142-local-agent-pi-opencode-design.md`. Read it before any task; the plan does not repeat its rationale

**Repos:** Tasks 1 to 7 in kcap-cli (this worktree, branch `tony/headless-pi-opencode`). Task 8 in kcap-server, in a fresh worktree off `origin/main`, branch `tony/ai-5142-local-agent-pi-opencode`

## Global Constraints

- Harness tokens: `pi`, `opencode`. Labels: `Pi`, `OpenCode`
- Tier names, case-insensitive: `haiku`, `sonnet`, `opus` → empty model for `pi` and `opencode`
- Pi argv, in this order: `-p --mode json --no-approve --no-tools --no-extensions --no-skills --no-prompt-templates --no-themes --no-context-files --no-session --offline --system-prompt <text> --append-system-prompt ""`, then `--model <m>` when not blank. Prompt on stdin, UTF-8 without BOM
- Pi neutral system prompt when the request has none: `Answer the request directly.`
- OpenCode argv: `run --format json --pure --print-logs --log-level ERROR --file <promptFile>`, then `-m <m>` when not blank, then the fixed message last
- OpenCode env set: `XDG_CONFIG_HOME`, `OPENCODE_CONFIG_DIR`, `XDG_DATA_HOME`, `OPENCODE_CONFIG_CONTENT`, `OPENCODE_PERMISSION`, `OPENCODE_DISABLE_PROJECT_CONFIG=1`, `OPENCODE_DISABLE_CLAUDE_CODE=1`, `OPENCODE_DISABLE_EXTERNAL_SKILLS=1`, `OPENCODE_DISABLE_AUTOUPDATE=1`, `OPENCODE_DISABLE_MODELS_FETCH=1`, and `OPENCODE_AUTH_CONTENT` when the user's `auth.json` exists. Env removed: `OPENCODE_CONFIG`
- Both: `KCAP_SKIP=1` (`HostedAgent.SkipVar`). Provider key variables are kept
- Per-run root: `<daemon state dir>/headless-runs`, owner-only and ancestor-checked on every run; any failure → `ClaudeCliFailure.ProcessFailure` before the process starts
- Schema nesting depth bound: 64
- Comments follow AGENTS.md: scarce, no ticket or history narration. One type per file
- Live tests spend money: gate `KCAP_PI_LIVE=1` and `KCAP_OPENCODE_LIVE=1`, model `anthropic/claude-haiku-4-5` (Pi: `--provider anthropic --model claude-haiku-4-5`), key from `ANTHROPIC_API_KEY`. Run each live test once; no retry without asking

## Review Focus

1. **A reply wrapped in a Markdown fence** (```` ```json … ``` ````) must validate as the JSON inside it, not fail as unparseable. Test in Task 1
2. **Pi whose configured default provider is broken** (a custom provider with a bad key, as on the test machine) must answer a classified failure from `errorMessage`, not success and not a hang. Test in Task 4, from the captured fixture
3. **OpenCode with no `auth.json`** (key only in the environment) must omit `OPENCODE_AUTH_CONTENT`, never pass `{}` or an empty string, and still run. Test in Task 5
4. **Cancellation mid-run** (the server cancels by request id) must kill the process and remove every per-run directory before the exception propagates. Test in Task 4 and Task 5
5. **A prompt with non-ASCII text** must reach the model byte-exact: Pi's stdin and OpenCode's prompt file are UTF-8 without BOM. Test in Task 4 and Task 5

---

### Task 1: Schema-checked structured replies

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Headless/JsonSchemaCheck.cs`
- Create: `src/Capacitor.Cli.Core/Harness/Headless/StructuredReply.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Headless/JsonSchemaCheckTests.cs`, `StructuredReplyTests.cs`

**Interfaces:**
- Produces: `static class JsonSchemaCheck { static string? UnsupportedKeyword(JsonElement schema); static bool Matches(JsonElement schema, JsonElement instance); }`. `UnsupportedKeyword` returns the first keyword outside the spec's vocabulary, or a non-local `$ref`, else null
- Produces: `static class StructuredReply { static string SchemaInstruction(string schema); static string? Accept(string reply, string schema); }`. `Accept` strips one surrounding code fence, parses, checks, and returns the re-serialised JSON or null

- [ ] **Step 1: Write failing tests**, as a table where it reads better:
  - `Matches` true and false per keyword: `type` (single and list, `integer` refusing `1.5`), `properties`, `required`, `additionalProperties` false and as a schema, `items`, `enum`, `const`, `anyOf`, `oneOf` (exactly one), `allOf`, `minimum`, `maximum`, `exclusiveMinimum`, `exclusiveMaximum`, `minLength`, `maxLength` (counted in text elements), `minItems`, `maxItems`, `pattern`
  - `$ref` to `#/$defs/x` and `#/definitions/x` accepted and refused correctly; `$defs` entries not applied to the instance; a self-referencing schema validates a nested value and fails at depth 64
  - `UnsupportedKeyword` returns `"if"` for a schema using `if`, `"$ref"` for `"$ref":"https://x"`, null for one using only vocabulary and annotations
  - `Accept`: fenced JSON accepted; `{}` against a schema requiring `title` → null; prose → null
- [ ] **Step 2: Run, expect FAIL** — `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit -- --treenode-filter "/*/*/JsonSchemaCheckTests/*"` and the same for `StructuredReplyTests`
- [ ] **Step 3: Implement** over `JsonElement`, recursive with a depth counter; `pattern` via `Regex` with `RegexOptions.None` and a 1-second match timeout
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `[AI-5142] Check structured replies against their schema`

### Task 2: Failure classification shared by both runners

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Headless/HeadlessFailureClassifier.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Headless/HeadlessFailureClassifierTests.cs`

**Interfaces:**
- Produces: `static ClaudeCliFailure Classify(int? statusCode, string? message)`: 401 or 403 → `NotSignedIn`; 429, or a message containing `credit balance`, `quota`, `usage limit`, `rate limit` (case-insensitive) → `SpendBudget`; a message containing `no models available` or `no api key` → `NotSignedIn`; else `ProcessFailure`
- Produces: `static int? StatusFromMessage(string? message)`: a leading HTTP status in Pi's `errorMessage` form (`"401 {…}"`), else null

- [ ] **Step 1: Write failing tests** (one table): `(401, null)→NotSignedIn`, `(null, "401 {\"type\":\"error\"…}")` via `StatusFromMessage`→401, `(400, "Your credit balance is too low…")→SpendBudget`, `(null, "Free usage limit reached…")→SpendBudget`, `(429, null)→SpendBudget`, `(500, "overloaded")→ProcessFailure`
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `[AI-5142] Classify headless failures from status and message`

### Task 3: Private per-run directories

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Headless/HeadlessRunRoot.cs`
- Create: `src/Capacitor.Cli.Core/Harness/Headless/AncestorChain.cs`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Headless/HeadlessRunRootTests.cs`, `AncestorChainTests.cs`

**Interfaces:**
- Consumes: `OwnerOnlyDirectory.CreateVerified(string)`, `OwnerOnlyDirectory.Violation(string)`, `OwnerOnlyDirectory.OwnerSids()`
- Produces: `static class AncestorChain { static string? Violation(string path); }` — the spec's rule: Unix, every ancestor owned by the current uid or 0 and not group/other-writable unless sticky; Windows, no `DELETE_CHILD`, `DELETE`, `WRITE_DAC`, `WRITE_OWNER` (directly, inherited or via generic rights) granted to anyone but the owner SIDs and `CREATOR OWNER`. Returns a description of the first violation, else null
- Produces: `sealed class HeadlessRunRoot(string stateDir)` with `HeadlessRunDirectory Create(string prefix)` (throws `HeadlessRunRefusedException` with the reason) and `void SweepStale(string currentEpoch)`; `sealed class HeadlessRunDirectory : IDisposable { string Path { get; } HeadlessRunDirectory Child(string name); }` whose `Dispose` deletes the tree without following links (reuse `OpenCodeReviewerConfigDir`'s no-follow delete by moving it to a shared internal helper in Core, with that class calling it)
- Produces: `sealed class HeadlessRunRefusedException(string reason) : Exception`

- [ ] **Step 1: Write failing tests** (POSIX; Windows ACL cases skip off Windows, as other owner-only tests do):
  - `Create` makes `headless-runs` and the run directory 0700 and empty
  - a `headless-runs` at 0777 → refused, even though the child could be 0700
  - a 0700 `headless-runs` under a 0777 non-sticky state dir → refused; under a 1777 state dir → allowed
  - `Dispose` removes the tree and leaves a symlink's target untouched
  - `SweepStale` removes directories of another epoch and keeps the current one's
  - Windows: a parent granting `Users` delete-child → refused; a volume root granting `Users` create-folder only → allowed
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.** Directory names `<prefix>-<epoch>-<guid>`; check the ancestor chain of `headless-runs` after creating it and before creating any child
- [ ] **Step 4: Run, expect PASS**, and `OpenCodeReviewerConfigDirTests` still pass
- [ ] **Step 5: Commit** `[AI-5142] Add a private, ancestor-checked root for headless runs`

### Task 4: Pi runner

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Pi/PiCliRunner.cs`, `PiJsonEvents.cs`
- Create fixtures: `test/fixtures/pi-headless/{success,unauthorised,credit-low,custom-provider-broken}.jsonl`, captured from real runs, with request ids, organisation ids and absolute paths replaced by placeholders
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Pi/PiCliRunnerTests.cs`, `PiJsonEventsTests.cs`, `PiCliRunnerLiveTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3
- Produces: `static Task<ClaudeCliOutcome> PiCliRunner.RunDetailedAsync(string prompt, TimeSpan timeout, TimeProvider time, Action<string> log, HeadlessRunRoot runs, string? model, string? jsonSchema, string? systemPrompt, string executable, CancellationToken ct)`
- Produces: `internal static List<string> BuildArgs(string systemPrompt, string? model)`, `internal static string BuildPrompt(string prompt, string? jsonSchema)`
- Produces: `sealed class PiJsonEvents { void Accept(string line); string? Reply; string? StopReason; string? ErrorMessage; ClaudeCliUsage? Usage; }` — read from the last `agent_end`: assistant messages' text, `stopReason`, `errorMessage`, summed `usage` (input, output, cacheRead, cacheWrite, `cost.total`)

- [ ] **Step 1: Write failing unit tests:** `BuildArgs` equals the Global Constraints vector exactly, with and without a model, and with the neutral system prompt; `PiJsonEvents` over each fixture gives the expected reply, stop reason, usage and cost (`success`: 4,747 tokens, $0.0061); the outcome mapping: `success` → result, `unauthorised` → `NotSignedIn`, `credit-low` → `SpendBudget`, `custom-provider-broken` → a failure, never a result; a schema reply of the wrong shape → `OutputUnparseable`; an unsupported keyword → `ProcessFailure` with no process started
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement** on `CodexCliRunner`'s process shape (stdout read without a token, kill on timeout and on cancellation, bounded post-kill read), with stdin encoding UTF-8 without BOM, the working directory a `HeadlessRunRoot.Create("pi")` disposed in `finally`, and `KCAP_SKIP=1` in the environment
- [ ] **Step 4: Write live tests** (`KCAP_PI_LIVE=1`, POSIX), one run each: plain answer; schema answer; non-ASCII prompt echoed back exactly; a planted file in the working directory is not in the reply; a planted `SYSTEM.md` and `APPEND_SYSTEM.md` in a temp `PI_CODING_AGENT_DIR` (copying only `auth.json` and `models.json` there) leave their marker tokens out of the reply; no file appears under that directory's `sessions/`; a planted `npmCommand` marker records only `root -g`; a 2-second deadline kills the run and the run directory is gone; cancellation after start throws and the directory is gone
- [ ] **Step 5: Run unit tests, expect PASS; run each live test once,** with the key from `~/dev/headless-eval-key` loaded into `ANTHROPIC_API_KEY` without printing it, and record the total cost from the outcomes
- [ ] **Step 6: Commit** `[AI-5142] Run headless prompts on Pi`

### Task 5: OpenCode runner

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/OpenCode/OpenCodeCliRunner.cs`, `OpenCodeJsonEvents.cs`, `OpenCodeHeadlessConfig.cs`
- Create fixtures: `test/fixtures/opencode-headless/{success.jsonl,unauthorised.jsonl,usage-limit.stderr.log}`, redacted as in Task 4
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/OpenCode/OpenCodeCliRunnerTests.cs`, `OpenCodeJsonEventsTests.cs`, `OpenCodeHeadlessConfigTests.cs`, `OpenCodeCliRunnerLiveTests.cs`

**Interfaces:**
- Consumes: Tasks 1 to 3, `OpenCodePaths`, `OpenCodeReviewerPermissions.ForbiddenTools`
- Produces: `static Task<ClaudeCliOutcome> OpenCodeCliRunner.RunDetailedAsync(string prompt, TimeSpan timeout, TimeProvider time, Action<string> log, HeadlessRunRoot runs, OpenCodePaths paths, string? model, string? jsonSchema, string? systemPrompt, string executable, CancellationToken ct)`
- Produces: `sealed record OpenCodeHeadlessConfig(string ConfigContent, string Permission, string? AuthContent)` with `static OpenCodeHeadlessConfig Build(OpenCodePaths paths, IReadOnlyDictionary<string, string?> inheritedEnv)` — reads `model` and `provider` from the spec's layers in its order (JSONC), throws `HeadlessRunRefusedException` naming an unreadable layer; `ConfigContent` holds `tools` all off, `permission` deny-all and the two copied keys
- Produces: `sealed class OpenCodeJsonEvents { void Accept(string line); string? Reply; int? ErrorStatus; string? ErrorMessage; ClaudeCliUsage? Usage; }` and `static string? OpenCodeLimitLog.Match(string stderrLine)`, returning the message of a `stream error` line that `HeadlessFailureClassifier` calls `SpendBudget`, else null

- [ ] **Step 1: Write failing unit tests:**
  - `OpenCodeHeadlessConfig.Build`: `model` from `opencode.json` overridden by a file named in `OPENCODE_CONFIG` overridden by an inherited `OPENCODE_CONFIG_CONTENT`; `provider` merged by id; JSONC comments and trailing commas parsed; a malformed layer refused naming its path; no layers → no `model` key; `AuthContent` null when `auth.json` is absent (Review Focus 3); `Permission` denies `*` and every `ForbiddenTools` entry
  - The environment the runner builds: every Global Constraints variable present, `OPENCODE_CONFIG` absent, an inherited `OPENCODE_PERMISSION` of `{"*":"allow"}` overwritten
  - `OpenCodeJsonEvents` over the fixtures: `success` reply and usage (16,748 tokens, $0.0123), `unauthorised` → status 401
  - `OpenCodeLimitLog.Match` on the `usage-limit` log line → the limit message
  - argv exactly as Global Constraints; the prompt file is UTF-8 without BOM and holds the system-prompt preamble, the prompt and the schema instruction
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement.** Three child directories of one `HeadlessRunRoot.Create("opencode")`: `cwd` (holding `prompt.md`), `config`, `data`. Read stderr line by line; on `OpenCodeLimitLog.Match`, kill and return `SpendBudget` with usage so far. Log once when a managed config file exists (`/Library/Application Support/opencode`, `/etc/opencode`, `%ProgramData%\opencode`)
- [ ] **Step 4: Confirm the lockdown answers on a paid model** with one plain-prompt live run. If it hangs or refuses with every tool off, drop only the `tools` block (permissions stay), record that in the spec's Lockdown section, and rerun once
- [ ] **Step 5: Write the remaining live tests** (`KCAP_OPENCODE_LIVE=1`, POSIX), one run each: schema answer; a planted file not in the reply; with a temp config dir holding an MCP server whose command writes a marker, inherited via `OPENCODE_CONFIG`, the marker never appears; inherited `OPENCODE_PERMISSION={"*":"allow"}` and a prompt asking to write a file leave the working directory unchanged; the run's session is absent from the user-side `opencode.db` (a temp `XDG_DATA_HOME` stands in for the user's) and `OpenCodeImportSource` discovery over it finds nothing; a 40,000-character prompt is answered; non-ASCII echoed exactly; deadline and cancellation remove every directory
- [ ] **Step 6: Run unit tests, expect PASS; run each live test once,** recording total cost
- [ ] **Step 7: Commit** `[AI-5143] Run headless prompts on OpenCode`

### Task 6: Daemon wiring

**Files:**
- Create: `src/Capacitor.Cli.Daemon/Harness/Pi/PiHeadlessHarness.cs`, `src/Capacitor.Cli.Daemon/Harness/OpenCode/OpenCodeHeadlessHarness.cs`
- Modify: `src/Capacitor.Cli.Daemon/DaemonRunner.cs` (registrations beside Codex's at ~631; `HeadlessRunRoot` singleton over the state dir; `SweepStale` at start beside `OpenCodeReviewerConfigDir.SweepStale`)
- Test: `test/Capacitor.Cli.Daemon.Tests.Unit/Harness/Pi/PiHeadlessHarnessTests.cs`, `.../OpenCode/OpenCodeHeadlessHarnessTests.cs`; extend `Services/HeadlessHarnessAvailabilityTests.cs`

**Interfaces:**
- Consumes: Tasks 4 and 5; `DaemonConfig.PiPath`, `DaemonConfig.OpenCodePath`, `HarnessRegistry.LaunchCommand(HarnessId.Pi|HarnessId.OpenCode, …)`
- Produces: `IHeadlessHarness` with `Harness` `"pi"` and `"opencode"`; a `HeadlessRunRefusedException` becomes `ClaudeCliOutcome(null, ProcessFailure, Error: reason)`

- [ ] **Step 1: Write failing tests,** mirroring `CodexHeadlessHarnessTests`: token, availability from the resolved binary, request fields passed through (model, schema, system prompt). Availability table: binaries `{pi}` → `["pi"]`, `{opencode, claude}` → `["claude","opencode"]`
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement and register**
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `[AI-5142] Offer Pi and OpenCode as headless harnesses in the daemon`

### Task 7: Setup surfaces

**Files:**
- Modify: `src/Capacitor.Cli.Core/Setup/Intelligence/HeadlessHarnessProbe.cs` (constants `Pi = "pi"`, `OpenCode = "opencode"`, both detected), `IntelligenceAnswer.cs` (`Harnesses`), `src/Capacitor.App/ViewModels/Onboarding/IntelligenceOption.cs`, `src/Capacitor.Cli/Commands/IntelligenceQuestions.cs`
- Test: the existing tests of each file (find with `git grep -l HeadlessHarnessProbe -- test`)

**Interfaces:**
- Consumes: none
- Produces: card detail for `pi` and `opencode`: `Runs on this machine through {Label}'s own sign-in or key. Nothing goes anywhere it does not already send.`; Claude and Codex keep their current text

- [ ] **Step 1: Write failing tests:** `Detect` lists `opencode` and `pi` when their binaries resolve, ordinal order; `IntelligenceAnswer.Parse("pi")` and `("opencode")` are local-agent answers; labels and the card text above
- [ ] **Step 2: Run, expect FAIL**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run, expect PASS**
- [ ] **Step 5: Commit** `[AI-5142] Offer Pi and OpenCode in local intelligence setup`

### Task 8: Server (kcap-server)

**Files:**
- Modify: `src/Capacitor.Server.Services/Evals/Server/LocalAgent/LocalAgentHarnesses.cs`, `LocalAgentModels.cs`, `src/Capacitor.Ui/Components/Pages/Admin/Settings/AiSettingsPageBase.razor` (help text of `Evals:ServerRunner:LocalAgent:Model` and the per-feature local-agent model keys)
- Test: a `LocalAgentModelsTests.cs` beside `DaemonChatClientTests.cs` in `test/Capacitor.Server.Services.Tests/Evals/LocalAgent/`; extend `test/Capacitor.Server.Tests.Evals/Server/ServerSettingsRegistryLocalAgentTests.cs`; create `test/Capacitor.Server.Services.Tests/Evals/LocalAgent/LocalAgentSchemaVocabularyTests.cs`

**Interfaces:**
- Produces: `LocalAgentHarnesses.Pi = "pi"`, `LocalAgentHarnesses.OpenCode = "opencode"`, `All = [Claude, Codex, Pi, OpenCode]`
- Produces: `Route("pi"|"opencode", tier)` → `("", null)`; any other model → `(model, null)`
- Help text: `For Pi and OpenCode, a tier name (haiku, sonnet, opus) runs the harness's own default model; otherwise enter the model as that harness names it, for OpenCode provider/model.`

- [ ] **Step 1: Make the worktree** (`git worktree add` off `origin/main`, then `git submodule update --init src/cli`); read `docs/gotchas/SUBSYSTEMS.md` for the evals subsystem and `docs/TESTING.md`
- [ ] **Step 2: Write failing tests:** `Route` table for all four harnesses and the three tiers plus `anthropic/claude-sonnet-4-5`; the registry accepts `pi` and `opencode`; the vocabulary test collects every schema the local agent can be sent (the tool schemas in `Evals/Server/Evidence/JudgeToolSchemas.cs` and `EvidenceVerdictTool.cs`, and the JSON response formats of every `GetResponseAsync<T>` reachable from a local-agent consumer) and asserts each uses only the spec's vocabulary, the same list as Task 1's, copied with a comment naming `JsonSchemaCheck` as its other half
- [ ] **Step 3: Run each test project method-targeted, expect FAIL** — never the full suite locally
- [ ] **Step 4: Implement**
- [ ] **Step 5: Run, expect PASS**; run `dotnet run --project test/Capacitor.Source.Tests/Capacitor.Source.Tests.csproj`
- [ ] **Step 6: Commit** `[AI-5142] Accept Pi and OpenCode as local-agent harnesses`

### Finishing

- [ ] Push both branches; open the kcap-cli PR (`[AI-5142] Run the local agent on Pi and OpenCode`, `Fixes AI-5142` and `Fixes AI-5143`) and the kcap-server PR (`[AI-5142] Accept Pi and OpenCode as local-agent harnesses`, `Part of AI-5142`), descriptions per `.github/PULL_REQUEST_TEMPLATE.md`
- [ ] Offer a code-review flow on each PR; merge only on instruction
