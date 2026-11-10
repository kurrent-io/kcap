# macOS App Takes Over an npm or Script kcap Install — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** When the macOS desktop app finds an npm or script kcap install, it moves the daemon service, the agent integrations and every PATH onto its bundled CLI and removes the old install, silently and recoverably

**Architecture:** The daemon move is a CLI verb: `kcap daemon service repoint --from <dir>`, which today serves only Windows Scheduled Tasks, gains a launchd branch built on `LaunchdServiceManager.RefreshUnit`, and the app runs it through the mutation lane like its other service verbs. A new `Capacitor.App.Services.CliAdoption` area gathers evidence, plans a batch in a pure preflight, and runs integrations, PATH links, the repoint, consumer and reference checks and deletion, recording progress in the config directory. Root-owned installs get a privileged helper behind one administrator prompt in the last stage

**Tech Stack:** .NET 10 NativeAOT, Avalonia desktop app, TUnit, launchctl, osascript

**Spec:** `docs/superpowers/specs/2026-11-04-app-adopts-cli-install-design.md` (GitHub issue #2373). Branch from `origin/main` at or after `7b0b8573f`, which includes PR #2361 (`DaemonOnOtherInstall`, `ProcessExecutable`)

## Delivery

Three PRs, in order. Each leaves the product working:

- **PR A — CLI and Core groundwork (Tasks 1–4).** Ships the spec and this plan. Useful alone: a bundle move benefits from Task 1, uninstall from Task 2, and `repoint --from` works on macOS
- **PR B — the switch for user-owned installs (Tasks 5–12).** A batch containing a root-owned install is held untouched (`adoption_held reason=administrator_support_pending`)
- **PR C — root-owned installs (Tasks 13–14).** Removes that hold

PRs A and B carry `Part of #2373` and the Linear id on their reference line; PR C carries `Closes #2373`

## Global Constraints

- macOS only: every app entry point returns early unless `OperatingSystem.IsMacOS()`
- Skipped entirely when `KCAP_APP_CLI_PATH` is set, or `InstallLocation.Classify(root, home)` is not a passing installed bundle
- Only npm and script layouts (`NpmInstallLayout.IsPlatformBinary`, `ScriptInstallLayout.FromBinary`) are ever touched; Homebrew, dev builds and copied binaries never
- An install newer than the app's CLI (`PrereleaseSemver.IsNewer(installVersion, appVersion)`) holds the whole batch, changing nothing
- Every daemon stop or start goes through `DaemonMutationLane.RunAsync`, classified by the lane's shared evidence predicate; nothing signals a daemon process
- The only prompt is macOS's administrator password prompt, once, for a root-owned install; a notice is shown once per outcome
- Order inside a batch: preflight → record intent → integrations (cutover) → PATH links → repoint → consumer and reference checks → deletion → switched
- Script installs lose only `versions`, `current` and `install.json`; npm installs lose their package folders under `<prefix>/lib/node_modules/@kurrent/`
- Durable files are written with `AtomicFile.Replace(path, text, mode: 0600, followLink: false, durable: true)`; persisted JSON uses source-generated contexts; no `JsonArray` collection expressions
- Paths are compared through `CanonicalPath.Resolve` / `CanonicalPath.IsWithin`, never `FileInfo.ResolveLinkTarget`, which resolves only the last component
- Comments follow `CLAUDE.md`'s rules: scarce, no history, no spec coordinates
- App unit tests run with session variables stripped: `env -u KCAP_SESSION_ID -u KCAP_DAEMON_ID -u KCAP_DAEMON_NAME -u KCAP_PROFILE dotnet run --project test/Capacitor.App.Tests.Unit/Capacitor.App.Tests.Unit.csproj -- --treenode-filter "/*/*/<Class>/*"`
- After each PR: `dotnet publish src/Capacitor.Cli/Capacitor.Cli.csproj -c Release 2>&1 | grep -E 'IL[23][01][0-9]{2}'` prints nothing
- Commit subjects end with ` (#2373)` and stay within 80 characters

## Review Focus

1. **A daemon too old to report `Busy`.** `DaemonLifecycleController` falls back to `ActiveAgents > 0 || Pending > 0`, so an old npm daemon with idle hosted agents never reaches a safe point. Expected: the batch stays pending, logs `adoption_held reason=busy` once per launch, and goes ahead the first time the daemon reports no agents. Test in Task 9
2. **The login-shell probe answers null.** Without a terminal PATH, preflight cannot map where `kcap` resolves. Expected: nothing changes, the batch is held with `reason=path_unknown`, retried next launch. Test in Task 7
3. **Paths with spaces and symlinked prefixes** (a home folder with a space, `~/.nvm` reached through a link). Expected: link swaps, reference scans and the privileged script handle both; a reference naming the install through its real path still counts. Tests in Tasks 5, 10 and 13
4. **Harnesses that run under an interpreter or retitle themselves.** Gemini, Copilot and Pi run as `node`, OpenCode as `bun`, Claude's native build runs as its version number. A process-name match alone misses them, and the deletion would remove a plugin a live session reads. Expected: Core's process matcher (Task 3) recognises each. Test in Task 10
5. **The app started hidden in the menu bar.** `AppNotifier` drops messages with no subscriber, so a once-per-outcome notice posted then is never seen. Expected: the notice waits until a main window has subscribed, and only then is recorded as shown. Test in Task 12

---

## PR A — CLI and Core groundwork

### Task 1: Repoint Claude's own plugin records

`ClaudePluginWriter.Install` edits `settings.json` only; `plugins/known_marketplaces.json` (`installLocation`) and `plugins/installed_plugins.json` (`installPath`) keep naming the previous plugin folder. Both the default Claude directory and every registered account reach `ClaudePluginWriter.Install` (`SetupCommand.InstallPlugin`, `AccountWiring`), so the repoint goes there

**Files:**
- Create: `src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginRecords.cs`
- Modify: `src/Capacitor.Cli.Core/Harness/Claude/ClaudePluginWriter.cs` (`Install`), `src/Capacitor.Cli/Commands/SetupCommand.cs:3397`, `src/Capacitor.Cli.Core/Accounts/AccountWiring.cs:129`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Harness/Claude/ClaudePluginRecordsTests.cs`

**Interfaces:**
- Produces: `public static SettingsEdit ClaudePluginRecords.Repoint(string claudeHome, string pluginPath)`
- Produces: `ClaudePluginWriter.Install(string settingsPath, string? claudeHome, string marketplacePath)` — `claudeHome` is the caller's `ClaudePaths.Home` for user scope and null for project scope; a `Failed` repoint makes `Install` return `Failed`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task The_kcap_directory_marketplace_follows_the_settings_source_path()
// known_marketplaces.json["kcap"] = { source: { source: "directory", path: OLD }, installLocation: OLD } → Repoint(home, NEW) == Changed; both NEW; other keys byte-identical
[Test] public async Task The_kcap_at_kcap_install_record_under_the_old_folder_moves_and_a_cache_path_stays()
[Test] public async Task Legacy_kurrent_and_kapacitor_marketplace_keys_are_repointed()
[Test] public async Task A_home_without_plugin_records_is_unchanged_and_creates_no_file()
[Test] public async Task Malformed_records_fail_without_writing()
[Test] public async Task A_null_claude_home_never_touches_records()                      // project scope
```

- [ ] **Step 2: Run them to verify they fail**

Run: `dotnet run --project test/Capacitor.Cli.Core.Tests.Unit/Capacitor.Cli.Core.Tests.Unit.csproj -- --treenode-filter "/*/*/ClaudePluginRecordsTests/*"`
Expected: build error, `ClaudePluginRecords` not defined

- [ ] **Step 3: Implement.** Records are found by key (marketplaces `kcap`, `kurrent`, `kapacitor`; plugins `kcap@kcap` and the legacy keys `ClaudePluginWriter` already removes), not by folder contents, and each is edited through `JsonSettingsFile.Edit`. Thread `claudeHome` through both callers
- [ ] **Step 4: Run the tests to verify they pass**, plus `ClaudePluginWriterTests`, `ClaudePluginInstallerTests` and `AccountWiringTests`
- [ ] **Step 5: Check Claude's own behaviour.** With Claude Code installed, point `settings.json` at a new folder, run `claude` once, and read `known_marketplaces.json`. If Claude rewrites `installLocation` from `settings.json` itself, say so in the PR description: the records repoint then only covers the window before Claude next starts, which Task 10's check still needs
- [ ] **Step 6: Commit** — `git commit -m "Repoint Claude's plugin records when kcap's plugin folder moves (#2373)"`

### Task 2: Adopted-link inventory behind one link-remover factory

Links the switch makes outside `KcapLinkFolders.For(home)` (a custom npm prefix, nvm's `bin`) must be found by a later bundle move and by every uninstall path, while a batch is still pending. `KcapLinkFolders.For` itself stays as it is: `PathShimInstaller.StableFolders()` reads it to decide which folders are stable

**Files:**
- Create: `src/Capacitor.Cli.Core/Setup/AdoptedLinkInventory.cs`
- Modify: `src/Capacitor.Cli.Core/Setup/KcapLinkRemover.cs` (factory, prune on remove), `src/Capacitor.Cli/Commands/CommandServices.cs:170`, `src/Capacitor.App/Platform/Mac/MacAppRemoval.cs:38`, `src/Capacitor.App/Platform/Linux/LinuxAppRemoval.cs:45`, `src/Capacitor.App/App.axaml.cs:1534`, `src/Capacitor.App/Services/ApplicationReferences.cs:82`
- Test: `test/Capacitor.Cli.Core.Tests.Unit/Setup/AdoptedLinkInventoryTests.cs`, `test/Capacitor.Cli.Core.Tests.Unit/Setup/KcapLinkRemoverTests.cs`, `test/Capacitor.App.Tests.Unit/ApplicationReferencesTests.cs`

**Interfaces:**
- Produces: `public sealed class AdoptedLinkInventory(ConfigRoot config)` with `IReadOnlyList<string> Links()`, `void Add(string linkPath)`, `void Remove(string linkPath)`; file `adopted-kcap-links`, one absolute path per line, under `config.AcquireLock("adopted-kcap-links")`
- Produces: `public static KcapLinkRemover KcapLinkRemover.For(UserHome home, ConfigRoot config, string target)` — folders are `KcapLinkFolders.For(home)` plus the inventory's link folders; `Remove()` also drops each removed link from the inventory
- Produces: `public static IReadOnlyList<string> KcapLinkFolders.WithAdopted(UserHome home, ConfigRoot config)` for `ApplicationReferences`, which walks folders itself

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Added_links_survive_a_reload_and_removal_drops_only_that_link()
[Test] public async Task A_missing_or_unreadable_inventory_reads_as_empty()
[Test] public async Task The_factory_finds_and_removes_an_adopted_link_and_prunes_it()
[Test] public async Task The_factory_ignores_an_adopted_path_that_now_links_elsewhere()
[Test] public async Task A_bundle_move_repoints_an_adopted_link_outside_the_fixed_folders()     // ApplicationReferencesTests
```

- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement**, and switch all four `new KcapLinkRemover(...)` sites to `KcapLinkRemover.For`, so the uninstall preview (`App.axaml.cs:1534`) and the removal agree
- [ ] **Step 4: Run the tests to verify they pass**, plus `UninstallCommandTests`, `MacAppRemovalTests`, `LinuxAppRemovalTests`
- [ ] **Step 5: Commit** — `git commit -m "Track kcap links made outside the usual folders (#2373)"`

### Task 3: Move process and launchd parsing into Core

The app references only `Capacitor.Cli.Core`. The pieces the switch needs live in `Capacitor.Cli`, so they move, become public, and the CLI keeps calling them

**Files:**
- Create: `src/Capacitor.Cli.Core/Processes/ProcessArgs.cs` (from `ProcessHelpers.ReadProcArgsMac`, `ParseArgs`, `ScriptArgument`, `IsScriptRuntime`, `ScriptNameCandidates`), `src/Capacitor.Cli.Core/Processes/AgentProcessNames.cs` (from `ExecutableNameCandidates`, `MatchesAgentName`, `ProcessNamesOf`, `VersionedBinaryPrefixOf`), `src/Capacitor.Cli.Core/Setup/LaunchdPrint.cs` (from `LaunchdUnit.PidFromPrint`, `LoadedProgram`, `ClassifyPrint`, `BinaryFromPlist`, `EnvFromPlist`)
- Modify: `src/Capacitor.Cli/ProcessHelpers.cs`, `src/Capacitor.Cli/Services/LaunchdUnit.cs` (delegate)
- Test: the existing `ProcessHelpersTests` and `LaunchdUnitTests` move to `test/Capacitor.Cli.Core.Tests.Unit/Processes/` and `.../Setup/` with the code

**Interfaces:**
- Produces: `public static IReadOnlyList<string>? ProcessArgs.Read(int pid)`, `public static string? ProcessArgs.Script(IReadOnlyList<string> argv)`, `public static bool ProcessArgs.IsScriptRuntime(string argv0)`
- Produces: `public static bool AgentProcessNames.Matches(string comm, string vendor)`, `public static IReadOnlyList<string> AgentProcessNames.Candidates(string executable)`
- Produces: `public static int? LaunchdPrint.Pid(string print)`, `public static string? LaunchdPrint.LoadedProgram(string print)`, `public static LabelProbe LaunchdPrint.Classify(int exit, string stdout, string stderr)` (moves `LabelProbe` with it), `public static string? LaunchdPrint.Binary(string plistXml)`, `public static IReadOnlyDictionary<string, string>? LaunchdPrint.Environment(string plistXml)`

- [ ] **Step 1: Move the tests first**; they fail to build against the new names
- [ ] **Step 2: Move the code** without changing behaviour; the old members delegate or are removed where every caller moved
- [ ] **Step 3: Run the moved suites and the full CLI unit suite** — all pass
- [ ] **Step 4: Commit** — `git commit -m "Move process and launchd parsing into Core (#2373)"`

### Task 4: `daemon service repoint --from` for launchd

`RepointFrom` (`DaemonServiceCommands.cs:867`) repoints only Scheduled Tasks and tells a launchd user to reinstall. `LaunchdServiceManager.RefreshUnit` already does the launchd half: it rewrites `ProgramArguments[0]` through its `stabilize` hook with `LaunchdUnit.WithBinary`, keeping every other byte; stops a running daemon with `StopForReload(id, "now", whenIdle: true)`, which keeps agents and turns a `busy:` refusal into `Deferred`; holds the service lock; and on a failed bootstrap writes the original plist back and reloads it

**Files:**
- Modify: `src/Capacitor.Cli/Commands/DaemonServiceCommands.cs` (`RepointFrom`), `src/Capacitor.Cli/Services/LaunchdServiceManager.cs` (`RefreshUnit` gains the two options below), `README.md` (the `daemon service repoint` entry), `src/Capacitor.Cli.Core/Resources/help-daemon.txt` if it lists `repoint`
- Test: `test/Capacitor.Cli.Tests.Unit/Services/LaunchdServiceManagerRepointTests.cs`, `test/Capacitor.Cli.Tests.Unit/Commands/DaemonServiceRepointTests.cs`

**Interfaces:**
- Produces: `RefreshUnit(..., bool leaveStoppedUnloaded = false, string? preimagePath = null, Func<string, string?>? amend = null)` — new parameters inserted before `amend`, the last one
  - `preimagePath`: the original plist is written there (`AtomicFile.Replace`, durable, 0600) before anything is stopped, and deleted once the job is reloaded or restored
  - `leaveStoppedUnloaded`: a loaded job with no running daemon is booted out and left unloaded with the rewritten plist (returns `UnitRefresh.NotLoaded`), never bootstrapped, since `RunAtLoad` would start it
- Produces: on launchd, `RepointFrom(dir)` calls `RefreshUnit` for every unit whose binary is `CanonicalPath.IsWithin(binary, dir)`, with `stabilize` mapping such a binary to `UnitIdentity.ResolveDaemonBinary()`, `leaveStoppedUnloaded: true` and `preimagePath = <plist>.kcap-repoint-preimage`. Before repointing a unit, a preimage file left by an interrupted run is settled: when the job is not loaded and running the plist on disk, the preimage is written back and the job restored to how it was. Exit 0 when every unit is repointed or was already; exit 3 when a daemon was busy (`Deferred`), unit unchanged; exit 1 otherwise

- [ ] **Step 1: Write the failing tests** (launchctl faked like the existing `LaunchdServiceManager` refresh tests)

```csharp
[Test] public async Task A_running_unit_under_dir_is_repointed_with_only_its_program_changed()
[Test] public async Task A_busy_daemon_leaves_the_unit_unchanged_and_exits_3()
[Test] public async Task A_loaded_stopped_job_is_booted_out_and_left_unloaded_without_a_bootstrap()
[Test] public async Task An_unloaded_unit_is_rewritten_and_left_unloaded()
[Test] public async Task A_failed_bootstrap_restores_the_original_plist_and_its_running_state()
[Test] public async Task The_preimage_exists_before_the_stop_and_is_gone_after_success()
[Test] public async Task A_preimage_left_by_a_crash_is_restored_before_the_next_repoint()
[Test] public async Task Units_outside_dir_and_another_user_s_labels_are_untouched()
[Test] public async Task A_dir_reached_through_a_symlinked_parent_still_matches()
```

- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement**
- [ ] **Step 4: Run the tests to verify they pass**, plus the existing refresh and Windows repoint tests
- [ ] **Step 5: README and help** — `kcap daemon service repoint --from <dir>` now works on macOS: it points every launchd daemon service running a binary under `<dir>` at this install's daemon, keeping its agents, leaves a stopped one stopped, and exits 3 when a daemon is busy
- [ ] **Step 6: Commit** — `git commit -m "Repoint launchd daemon services from another kcap install (#2373)"`

**PR A:** the spec and this plan go in with Task 1

---

## PR B — the switch for user-owned installs

New app types live in `src/Capacitor.App/Services/CliAdoption/` (namespace `Capacitor.App.Services.CliAdoption`); App tests stay flat in `test/Capacitor.App.Tests.Unit/`, as the project's existing tests are

### Task 5: Identify an install and gather evidence

**Files:**
- Create: `CliAdoption/OtherCliInstall.cs`, `CliAdoption/OtherCliInstallKind.cs`, `CliAdoption/OtherCliInstallReader.cs`, `CliAdoption/AdoptionEvidence.cs` (the evidence records, read together), `CliAdoption/AdoptionEvidenceReader.cs`, `src/Capacitor.App/Services/Launchctl.cs`, `src/Capacitor.App/Services/EntryIdentity.cs`
- Modify: `src/Capacitor.App/Services/ApplicationReferences.cs` (use `Launchctl`), `src/Capacitor.App/Services/LocalSetup/StaleClientRepair.cs` (use `EntryIdentity` and the shared npm prefix)
- Test: `OtherCliInstallReaderTests.cs`, `AdoptionEvidenceReaderTests.cs`, `EntryIdentityTests.cs`

**Interfaces:**
- Produces:
  - `internal sealed class Launchctl(IProcessRunner runner, string? domain = null)` with `PrintAsync(string label)`, `BootoutAsync(string label)`, `BootstrapAsync(string plistPath)`, `KickstartAsync(string label)` — `ApplicationReferences`' `CtlAsync`/`PrintAsync` lifted out, parsed with `LaunchdPrint`
  - `internal sealed class EntryIdentity(IProcessRunner runner)` with `Task<string?> OfAsync(string entry, CancellationToken ct)` — `StaleClientRepair.IdentityAsync`'s `stat` of device, inode, modification and change times, lifted out
  - `internal static string? NpmPrefixOf(string entry)` on `OtherCliInstallReader`, `StaleClientRepair.NpmPrefix` lifted out
  - `public enum OtherCliInstallKind { Npm, Script }` (`InstallKind` lives in `Capacitor.Cli`, out of the app's reach)
  - `public sealed record OtherCliInstall(OtherCliInstallKind Kind, string Root, string Version, string CliBinary, string DaemonBinary, string Identity, IReadOnlyList<string> OwnedEntries)` — npm: `Root` = `<prefix>/lib/node_modules/@kurrent/kcap`, `OwnedEntries` = that folder plus `@kurrent/kcap-darwin-arm64` beside it when present, `Version` from the launcher's `package.json`; script: `Root` = the layout root, `OwnedEntries` = `versions`, `current`, `install.json`, `Version` = the `versions/<v>` `current` resolves to; `Identity` = `EntryIdentity` of `DaemonBinary`
  - `internal static Task<OtherCliInstall?> OtherCliInstallReader.FromBinaryAsync(string binaryPath, EntryIdentity identity, CancellationToken ct)` — `CanonicalPath.Resolve` first; anything but an npm or script layout is null
  - `public sealed record ServiceUnitFact(string PlistPath, string Name, string Program, string? StoredPath, string? KcapOnStoredPath, bool Loaded, int? JobPid, string? LoadedProgram)`
  - `public sealed record RunningDaemonFact(string Name, int Pid, string? StartToken, string? Executable)`
  - `public sealed record AdoptionEvidence(IReadOnlyList<OtherCliInstall> Installs, IReadOnlyList<ServiceUnitFact> Units, IReadOnlyList<RunningDaemonFact> Daemons, string? LoginPath, string? KcapOnLoginPath)` — the `KcapOn…` fields are the first `kcap` on that PATH as found, from `BinaryProbe.Searching(path).Resolve("kcap")`
  - `internal sealed class AdoptionEvidenceReader(IProcessRunner runner, ILoginShellProbe probe, DaemonStore store, UserHome home, string bundleRoot)` with `Task<AdoptionEvidence> ReadAsync(CancellationToken ct)`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task An_npm_platform_binary_reads_as_an_npm_install_with_both_package_folders()
[Test] public async Task A_nested_npm_layout_owns_only_the_launcher_folder()
[Test] public async Task A_script_current_link_reads_the_version_it_points_at()
[Test] public async Task A_homebrew_or_dev_binary_reads_as_nothing()
[Test] public async Task An_install_under_a_symlinked_nvm_folder_is_read_through_its_real_path()   // Review Focus 3
[Test] public async Task A_reinstall_at_the_same_path_has_a_different_identity()
[Test] public async Task A_mac_with_no_other_install_runs_no_launchctl()
// every plist names the bundle, the daemon runs the bundle, no foreign link: zero /bin/launchctl calls
[Test] public async Task Units_are_printed_only_once_an_install_is_found()
[Test] public async Task A_running_daemon_is_read_from_its_pid_file_with_its_executable()
[Test] public async Task The_login_PATH_and_each_unit_PATH_report_their_first_kcap_unresolved()
[Test] public async Task Evidence_from_a_unit_a_daemon_a_PATH_and_a_link_names_one_install_once()
```

- [ ] **Step 2: Run them to verify they fail**
- [ ] **Step 3: Implement.** Start the login-shell probe first and do the filesystem reads while it runs. Classify each plist's `ProgramArguments[0]` (`LaunchdPrint.Binary`), each running daemon's executable, each `KcapLinkFolders.For(home)` link and the login PATH's `kcap`; only when that yields an install, or a plist names something other than the bundle, print the `io.kurrent.kcap.daemon.*` labels for `Loaded`, `JobPid` and `LoadedProgram`
- [ ] **Step 4: Run the tests to verify they pass**, plus `ApplicationReferencesTests` and `StaleClientRepairTests`
- [ ] **Step 5: Commit** — `git commit -m "Read the evidence of another kcap install on macOS (#2373)"`

### Task 6: The durable record

**Files:**
- Create: `CliAdoption/AdoptionRecord.cs` (records and enums, read together), `CliAdoption/AdoptionRecordStore.cs`, `CliAdoption/AdoptionRecordJsonContext.cs`
- Test: `AdoptionRecordStoreTests.cs`

**Interfaces:**
- Produces:
  - `public enum InstallState { Pending, Left, Switched }`
  - `public enum LeftReason { OwnFolderOnPath, UnsupervisedDaemon, PromptCancelled, DeletionCutShort, SharedInstallPrivateApp }`
  - `public sealed record LinkChange(string Path, string Original, string Replacement)` (also used by Tasks 7, 8 and 13)
  - `public sealed record RecordedInstall(OtherCliInstall Install, InstallState State, LeftReason? Reason, string? HeldBy, int? HolderPid, string? HolderStartToken)`
  - `public sealed record PrivilegedOperation(string InstallRoot, int? Pid, string? StartToken, bool GoSent)` (used from Task 13)
  - `public sealed record AdoptionRecord(DateTime? CutoverUtc, IReadOnlyList<RecordedInstall> Installs, IReadOnlyList<LinkChange> Links, PrivilegedOperation? Privileged, IReadOnlyList<string> NoticesShown, bool MachineSwitched)` with `static AdoptionRecord Empty` and `bool Pending => !MachineSwitched && Installs.Any(i => i.State == InstallState.Pending)`
  - `internal sealed class AdoptionRecordStore(ConfigRoot config)` with `AdoptionRecord Load()` and `AdoptionRecord Update(Func<AdoptionRecord, AdoptionRecord> change)`; file `cli-adoption.json`, lock `config.AcquireLock("cli-adoption")`, written with `AtomicFile.Replace(..., mode: 0600, followLink: false, durable: true)`

Nothing records a waiting batch: it is recomputed from evidence on every launch and has nothing to recover. Aside names for deletion live in `ApplicationRetirement`'s own record format (Task 11)

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task A_record_round_trips_every_field()
[Test] public async Task A_missing_or_malformed_file_loads_empty_without_deleting_it()
[Test] public async Task The_file_is_owner_only()                         // 0600
[Test] public async Task Concurrent_updates_both_land()
```

- [ ] **Step 2–4:** fail, implement, pass
- [ ] **Step 5: Commit** — `git commit -m "Record the CLI takeover's progress durably (#2373)"`

### Task 7: Preflight

**Files:**
- Create: `CliAdoption/AdoptionPreflight.cs`, `CliAdoption/AdoptionPlan.cs`
- Test: `AdoptionPreflightTests.cs`

**Interfaces:**
- Consumes: `AdoptionEvidence`, `AdoptionRecord`
- Produces:
  - `public sealed record InstallPlan(OtherCliInstall Install, LeftReason? Left, bool NeedsAdministrator, IReadOnlyList<LinkChange> Links)` — `Links` replace each PATH location resolving into the install with a link to the bundle's `kcap`; a location outside `KcapLinkFolders.For(home)` is marked by `PathShimInstaller` needing a stable link (see `NeedsStableLink`)
  - `public sealed record AdoptionPlan(string? HoldReason, IReadOnlyList<InstallPlan> Installs, bool NeedsStableLink, IReadOnlyList<OtherCliInstall> Reported)` — `HoldReason` non-null means change nothing: `newer_install`, `path_unknown`, `administrator_support_pending` (until Task 14)
  - `public static AdoptionPlan AdoptionPreflight.Plan(AdoptionEvidence evidence, AdoptionRecord record, string appVersion, string bundleCli, bool appInSystemApplications, Func<string, bool> userCanWrite, IReadOnlyList<string> stableFolders)`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task No_other_install_plans_nothing()
[Test] public async Task An_install_newer_than_the_app_holds_the_batch_with_newer_install()
[Test] public async Task An_unknown_login_PATH_holds_with_path_unknown()                         // Review Focus 2
[Test] public async Task A_daemon_running_from_the_install_that_is_not_the_job_pid_leaves_it_UnsupervisedDaemon()
[Test] public async Task A_manual_daemon_beside_an_unloaded_plist_leaves_it_UnsupervisedDaemon()
[Test] public async Task A_PATH_entry_naming_the_install_folder_leaves_it_OwnFolderOnPath()       // <root>/current/bin
[Test] public async Task A_left_install_that_wins_the_login_PATH_does_not_hold_an_eligible_one()
[Test] public async Task Custom_prefix_and_nvm_links_are_planned_in_place_and_need_a_stable_link()
[Test] public async Task A_root_owned_folder_or_link_needs_administrator()
[Test] public async Task A_root_owned_install_with_the_app_in_user_Applications_is_left_SharedInstallPrivateApp()
[Test] public async Task An_install_only_a_service_names_with_no_kcap_on_any_PATH_needs_a_stable_link()
[Test] public async Task After_the_machine_switched_a_new_install_is_reported_not_planned()
[Test] public async Task A_recorded_install_whose_identity_changed_is_planned_afresh()
[Test] public async Task Until_PR_C_a_batch_needing_administrator_holds()
```

- [ ] **Step 2–4:** fail, implement as a pure function, pass
- [ ] **Step 5: Commit** — `git commit -m "Plan the CLI takeover batch from fresh evidence (#2373)"`

### Task 8: Integrations and the cutover

**Files:**
- Modify: `src/Capacitor.App/Services/ApplicationIntegrationRefresh.cs`
- Create: `CliAdoption/AdoptionIntegrations.cs`
- Test: `AdoptionIntegrationsTests.cs`, `ApplicationIntegrationRefreshTests.cs`

**Interfaces:**
- Produces: `internal Task<ProcessResult> ApplicationIntegrationRefresh.RunAsync(string cli, CancellationToken ct)` — takes the `app-integration-refresh-pending` lock for the run and deletes a pending marker that names this bundle, so a bundle-move refresh and an adoption refresh never run at once and the second one skips; `RunPendingAsync` uses it and no longer holds the lock on the UI thread
- Produces: `internal sealed class AdoptionIntegrations(ApplicationIntegrationRefresh refresh, string bundleCli, AdoptionRecordStore store, TimeProvider time)` with `Task<bool> RepointAsync(bool foundNamed, CancellationToken ct)` — runs the refresh only when the record has no cutover or `foundNamed` (a reference site from Task 10's read still names an old install); records `CutoverUtc` as the refresh's finish in those cases

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task The_first_completed_refresh_records_the_cutover_at_its_finish()
[Test] public async Task With_a_cutover_and_nothing_named_no_refresh_runs()
[Test] public async Task A_site_still_naming_the_old_install_reruns_the_refresh_and_moves_the_cutover()
[Test] public async Task A_failed_refresh_records_no_cutover_and_returns_false()
[Test] public async Task A_pending_bundle_move_marker_for_this_bundle_is_consumed_by_the_adoption_refresh()
```

- [ ] **Step 2–4:** fail, implement, pass
- [ ] **Step 5: Commit** — `git commit -m "Repoint integrations first and record the takeover cutover (#2373)"`

### Task 9: PATH links and the repoint through the lane

**Files:**
- Create: `src/Capacitor.Cli.Core/Setup/KcapLinkSwap.cs`, `CliAdoption/AdoptionPathStep.cs`, `CliAdoption/AdoptionRepoint.cs`
- Modify: `src/Capacitor.App/Services/ApplicationReferences.cs` and `src/Capacitor.App/Services/LocalSetup/StaleClientRepair.cs` (their staging-link swaps call `KcapLinkSwap.Replace`), `src/Capacitor.App/Services/Mutation/MutationModel.cs` (add `Repoint` after `StopForUpdate`), `src/Capacitor.App/Services/Mutation/DaemonMutationLane.cs` (dispatch and classify `Repoint`), `src/Capacitor.App/Services/KcapCli.cs` (`RepointFromAsync`)
- Test: `KcapLinkSwapTests.cs` (Core), `AdoptionPathStepTests.cs`, `AdoptionRepointTests.cs`, `DaemonMutationLaneTests.cs`

**Interfaces:**
- Produces: `public static void KcapLinkSwap.Replace(string path, string expected, string replacement)` — throws `IOException` when `path` no longer links to `expected`; a staging link renamed over `path`
- Produces: `internal sealed class AdoptionPathStep(ILoginShellProbe probe, AdoptedLinkInventory inventory, AdoptionRecordStore store, ICliPathInstaller shim)` with `Task<bool> ApplyAsync(AdoptionPlan plan, string bundleCli, CancellationToken ct)` and `Task PutBackAsync(CancellationToken ct)`
  - Records each `LinkChange` (and `inventory.Add` for one outside the fixed folders) before making it. Put-back reads each link and restores only one that still points at `Replacement`
  - Adds a `shim.InstallAsync(bundleCli)` link when `plan.NeedsStableLink`
  - Success: `probe.KcapPathAsync(forceRefresh: true)` and `BinaryProbe.Searching(unit.StoredPath).Resolve("kcap")` resolve, through `CanonicalPath.Resolve`, to `bundleCli` for the login PATH and every unit PATH whose location the batch replaced; otherwise `PutBackAsync` and `false`. Root-owned links are skipped (Task 14)
- Produces: `Task<ProcessResult> IKcapCli.RepointFromAsync(string dir, CancellationToken ct)` running `daemon service repoint --from <dir>`; `MutationVerb.Repoint` with `MutationRequest.RepointFrom` (a `string?` inserted before `RequestId`, the last parameter); the lane classifies it as it classifies `Reload`, adding that the observed daemon's executable (`ProcessExecutable.Of(hello.Pid)`) is the bundle's daemon; exit 3 maps to `Refused("daemon_busy", RecoverySurface.None)`
- Produces: `internal sealed class AdoptionRepoint(DaemonMutationLane lane, Func<DaemonActivity?> activity, ExpectedDaemonRestart expected)` with `Task<MutationOutcome> RunAsync(OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, MutationRequest request, CancellationToken ct)` — returns `Refused("busy")` without sending anything unless `activity()?.Busy == false`; returns `Refused("install_changed")` when `stillSame` (identity, version and job pid against the record) fails; otherwise holds `expected.HoldForUpdate()` and runs the lane request

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Each_location_is_replaced_at_its_own_path_and_both_PATHs_resolve_to_the_bundle()
[Test] public async Task A_link_changed_since_preflight_is_left_and_the_step_fails_with_everything_put_back()
[Test] public async Task An_nvm_location_keeps_its_replacement_gains_a_stable_link_and_enters_the_inventory_first()
[Test] public async Task A_service_only_install_gets_a_silent_stable_link()
[Test] public async Task A_failing_shim_or_profile_write_puts_back_and_fails()
[Test] public async Task A_unit_PATH_without_local_bin_still_resolves_through_the_replaced_location()
[Test] public async Task A_home_with_a_space_swaps_cleanly()                                        // Review Focus 3
[Test] public async Task Activity_busy_or_unknown_never_reaches_the_lane()                        // Review Focus 1
[Test] public async Task An_npm_update_since_preflight_refuses_install_changed()
[Test] public async Task A_busy_repoint_maps_to_daemon_busy()
[Test] public async Task A_repoint_whose_successor_runs_the_old_binary_is_not_a_success()          // DaemonMutationLaneTests
[Test] public async Task The_app_s_restart_recovery_is_held_while_the_repoint_runs()
```

- [ ] **Step 2–4:** fail, implement, pass (existing `ApplicationReferencesTests`, `StaleClientRepairTests` and `DaemonMutationLaneTests` stay green)
- [ ] **Step 5: Commit** — `git commit -m "Swap PATH links and repoint the daemon through the lane (#2373)"`

### Task 10: Consumers and references before deletion

**Files:**
- Create: `CliAdoption/AdoptionConsumers.cs`, `CliAdoption/ProcessFacts.cs`, `CliAdoption/AdoptionReferenceSites.cs`
- Test: `AdoptionConsumersTests.cs`, `AdoptionReferenceSitesTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record ProcessFact(int Pid, string? Executable, IReadOnlyList<string>? Args, DateTime StartedUtc)`; `internal static IReadOnlyList<ProcessFact> ProcessFacts.ForCurrentUser()` — this user's processes only (`proc_listpids(PROC_UID_ONLY, getuid())`), executable from `ProcessExecutable.Of`, start time in UTC, args from `ProcessArgs.Read` only when `ProcessArgs.IsScriptRuntime(executable)`
  - `public sealed record ReferenceSite(string Name, string? Text, bool Readable)`; `internal static Task<IReadOnlyList<ReferenceSite>> AdoptionReferenceSites.ReadAsync(UserHome home, IReadOnlyList<string> claudeHomes, Launchctl launchctl, CancellationToken ct)` — every plist in `~/Library/LaunchAgents`, the `launchctl print` of `io.kurrent.kcap.daemon.*` labels only, `~/.gitconfig`, and each harness's user-scope settings, hooks and MCP files from its `Paths`; Claude homes are the default and each registered account's
  - `internal static string? AdoptionConsumers.Holder(OtherCliInstall install, DateTime cutoverUtc, IReadOnlyList<ProcessFact> processes, IReadOnlyList<ReferenceSite> sites, HarnessRegistry harnesses, string bundleRoot)` — null when clear, otherwise what holds it (`pid:<n>` or `site:<name>`)

Rules: an executable within the install's root or an owned entry holds it; a harness process started before `cutoverUtc`, matched by `AgentProcessNames.Matches` against each `HarnessRegistry` entry's `CliBinary`, or by `ProcessArgs.Script(args)` for a script runtime, holds it; a site whose text contains the install root or its `CanonicalPath.Resolve`d form holds it; an unreadable site holds it; where `ClaudePluginInstaller.IsInstalled(settings)`, `EffectiveMcpJsonPath` must resolve into the bundle, or resolve nothing while `settings.json` names the bundle and no record names the old install

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task An_mcp_server_running_from_the_install_holds_it()
[Test] public async Task A_harness_started_before_the_cutover_holds_it_and_one_after_does_not()
[Test] public async Task Harnesses_under_node_and_bun_and_a_retitled_native_Claude_are_recognised() // Review Focus 4
[Test] public async Task Start_times_compare_in_utc()
[Test] public async Task A_reference_through_the_real_path_of_a_symlinked_prefix_holds_it()      // Review Focus 3
[Test] public async Task An_unreadable_site_holds_it()
[Test] public async Task A_loaded_job_still_naming_the_old_program_holds_it()
[Test] public async Task No_Claude_plugin_passes_and_an_enabled_plugin_with_no_install_record_passes_once_settings_name_the_bundle()
[Test] public async Task An_effective_payload_still_in_the_old_folder_holds_it()
[Test] public async Task Only_kcap_daemon_labels_are_printed()
```

- [ ] **Step 2–4:** fail, implement, pass
- [ ] **Step 5: Commit** — `git commit -m "Keep an old kcap install while anything still uses it (#2373)"`

### Task 11: Delete the old files through the retirement protocol

`ApplicationRetirement` already records an aside name, renames, re-verifies and deletes a bundle, with guards on the aside path (beside the original, the generated suffix, not a link). Deletion extracts that protocol rather than writing a second one

**Files:**
- Create: `src/Capacitor.App/Services/AsideRetirement.cs`
- Modify: `src/Capacitor.App/Services/ApplicationRetirement.cs` (use it), `CliAdoption/` (a deletion call in the coordinator, Task 12)
- Test: `AsideRetirementTests.cs`, `ApplicationRetirementTests.cs`

**Interfaces:**
- Produces: `internal sealed class AsideRetirement(ConfigRoot config, string recordName)` with `bool Retire(string entry, Func<string, bool> stillOurs)` and `void FinishInterrupted()` — the record names the entry and its aside before the rename; `stillOurs(asidePath)` re-verifies after it (npm: the aside's `package.json` names `@kurrent/kcap`; script: its `versions` folder or `install.json`); a tree that fails is moved back; an interrupted aside is deleted only when it passes `ApplicationRetirement`'s existing guards
- Each owned entry of an install is retired on its own, under record name `cli-adoption-retire`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Npm_package_folders_are_each_recorded_moved_checked_and_deleted()
[Test] public async Task A_script_root_keeps_everything_but_versions_current_and_install_json()
[Test] public async Task A_crash_right_after_a_rename_is_finished_on_the_next_launch()
[Test] public async Task A_crash_between_two_npm_package_moves_is_finished_on_the_next_launch()
[Test] public async Task A_tree_replaced_between_check_and_rename_is_moved_back()
[Test] public async Task A_recorded_aside_that_is_a_link_or_lacks_the_suffix_is_never_deleted()
```

- [ ] **Step 2–4:** fail, implement, pass (existing `ApplicationRetirementTests` stay green)
- [ ] **Step 5: Commit** — `git commit -m "Retire an old kcap install through recorded aside renames (#2373)"`

### Task 12: The coordinator and app wiring

**Files:**
- Create: `CliAdoption/CliAdoptionCoordinator.cs`, `CliAdoption/AdoptionNotices.cs`
- Modify: `src/Capacitor.App/App.axaml.cs` (construct after the lane with `laneProbe`; `BuildLifecycleController` reuses `laneProbe` instead of creating another; pass `FirstLook` into the shim offer's `phaseClosed` `Task.WhenAll`; start with `Task.Run` before the post-startup `ApplicationIntegrationRefresh` call), `README.md` (desktop app paragraph)
- Test: `CliAdoptionCoordinatorTests.cs`, `AdoptionNoticesTests.cs`, `ShimOfferCoordinatorTests.cs`

**Interfaces:**
- Consumes: Tasks 5–11
- Produces: `internal sealed class CliAdoptionCoordinator` with `Task FirstLook { get; }` (completes on every path: skipped, held, faulted, done), `void Start()`, `internal static readonly TimeSpan RecheckInterval = TimeSpan.FromMinutes(15)`
- Produces: `internal sealed class AdoptionNotices(IObservable<IAppNotifier?> notifier, AdoptionRecordStore store)` with `void Post(string outcomeKey, string message)` — waits for a notifier with a visible window, notifies, appends `outcomeKey` to `NoticesShown`; a key already shown is dropped

**On launch** (`RunOnceAsync`): `AsideRetirement.FinishInterrupted` → evidence → plan → when not held: record intent and claim `AppState.ShimOffered` → integrations → PATH → repoint (exit busy: pending) → read processes and sites once → `Holder` per install → retire clear installs → `Switched`, and `MachineSwitched` once every install is switched or left → notice. **While pending**, a timer runs every `RecheckInterval` and does only the deletion gate: when the recorded holder is a process whose `ProcessStartToken.Matches` still holds, it stops there; otherwise it reads processes and sites, reruns the refresh only when a site names the install, and retires what is clear. The timer exists only while the record is pending

Each event goes to `LifecycleLog.Write` as `adoption_found`, `adoption_held`, `adoption_integrations`, `adoption_path`, `adoption_repoint`, `adoption_kept`, `adoption_retired`, `adoption_left`, `adoption_switched` with `install`, `version`, `result` and `reason` fields

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task A_user_owned_npm_install_is_switched_end_to_end_and_noticed_once()
[Test] public async Task A_busy_repoint_keeps_PATH_and_integrations_and_stays_pending()
[Test] public async Task A_recheck_with_the_holder_still_alive_reads_nothing_else()
[Test] public async Task A_recheck_after_the_holder_exits_retires_without_a_second_notice()
[Test] public async Task No_timer_runs_when_nothing_is_pending()
[Test] public async Task The_shim_offer_waits_for_the_first_look_and_never_shows_during_a_switch()   // ShimOfferCoordinatorTests
[Test] public async Task FirstLook_completes_when_skipped_on_KCAP_APP_CLI_PATH_and_when_the_run_throws()
[Test] public async Task A_notice_posted_while_hidden_is_shown_when_the_window_appears()            // Review Focus 5
[Test] public async Task A_batch_needing_administrator_rights_changes_nothing()                    // until Task 14
[Test] public async Task An_install_appearing_after_the_machine_switched_is_noticed_once_and_kept()
[Test] public async Task A_reference_recreated_between_launches_holds_the_deletion()
```

- [ ] **Step 2–4:** fail, implement, pass; `KCAP_APP_CLI_PATH` tests use `EnvScope.Exclusive`
- [ ] **Step 5: README.** In the desktop app paragraph (`README.md:96`), say that when the app finds an npm or script install it moves the daemon, integrations and terminal `kcap` onto its own CLI and removes the old install, keeping the existing setup, and that a project-scope `kcap plugin install --project` must be re-run in that repository
- [ ] **Step 6: Full app suite and publish check** — the app suite passes; the publish grep prints nothing
- [ ] **Step 7: Commit** — `git commit -m "Take over a user-owned npm or script kcap install from the app (#2373)"`
- [ ] **Step 8: Live check on a Mac** with `npm install -g @kurrent/kcap` (Homebrew Node), `kcap setup`, a running hosted agent, then the app: the daemon runs the bundle's binary, `which kcap` resolves to a link to the bundle, the npm package folders are gone once pre-switch sessions end, and onboarding never appeared. Record the run in the PR description

---

## PR C — root-owned installs

### Task 13: The privileged helper

**Files:**
- Create: `CliAdoption/PrivilegedSwapScript.cs`, `CliAdoption/PrivilegedSwap.cs`, `src/Capacitor.App/Services/AdministratorPrompt.cs` (the cancelled-prompt rule, `(-128)` in stderr, lifted out of `StaleClientRepair`)
- Test: `PrivilegedSwapScriptTests.cs` (runs the rendered script with `/bin/bash` as the current user against `TempDir` trees; `[ParallelLimiter<SubprocessLimit>]`)

**Interfaces:**
- Produces: `public sealed record PrivilegedSpec(IReadOnlyList<LinkChange> Links, IReadOnlyList<string> Entries, string Marker, string ControlFifo, string StatusFile, TimeSpan GoWait)` — `Marker` is the file each aside tree must contain
- Produces: `internal static string PrivilegedSwapScript.Render(PrivilegedSpec spec)` — a bash script taking every path as a quoted positional argument: writes `$$` and `swapped` to `StatusFile` after the link swaps (each guarded by `test -L` and `readlink` equality); `read -t <GoWait>` from `ControlFifo`; on `go` renames each entry aside, checks `Marker`, deletes it, writes `deleted`; on `put-back`, EOF or timeout restores the links and writes `restored`
- Produces: `internal sealed class PrivilegedSwap(IProcessRunner runner, AdoptionRecordStore store)` with `Task<PrivilegedResult> RunAsync(PrivilegedSpec spec, Func<CancellationToken, Task<bool>> verify, CancellationToken ct)`; `public enum PrivilegedResult { Deleted, PutBack, Cancelled, CutShort }` — makes the FIFO owner-only, starts `osascript … with administrator privileges` without awaiting, records the helper's pid and `ProcessStartToken`, holds the FIFO open for writing, waits for `swapped`, runs `verify`, sends `go` or `put-back`, records `GoSent`, awaits the exit; `AdministratorPrompt.WasCancelled(result)` → `Cancelled`

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task Go_swaps_then_deletes_each_entry_after_its_marker_check()
[Test] public async Task Put_back_restores_every_link_and_keeps_the_files()
[Test] public async Task Closing_the_control_pipe_before_go_restores_the_links()
[Test] public async Task No_word_within_the_wait_restores_the_links()
[Test] public async Task A_link_changed_since_the_plan_is_left_and_the_rest_restored()
[Test] public async Task An_aside_tree_without_its_marker_is_moved_back()
[Test] public async Task Paths_with_spaces_and_quotes_are_passed_as_arguments()                   // Review Focus 3
[Test] public async Task Every_file_stays_reachable_until_go()
```

- [ ] **Step 2–4:** fail, implement, pass
- [ ] **Step 5: Commit** — `git commit -m "Add a one-prompt privileged helper for root-owned kcap installs (#2373)"`

### Task 14: Root-owned batches, left installs and Finish switching

**Files:**
- Modify: `CliAdoption/AdoptionPreflight.cs` (drop the `administrator_support_pending` hold), `CliAdoption/CliAdoptionCoordinator.cs`, `src/Capacitor.App/ViewModels/TrayViewModel.cs`, the tray menu view that lists `InstallShimCommand`, `README.md`
- Test: `CliAdoptionCoordinatorTests.cs`, `AdoptionPreflightTests.cs`, `TrayViewModelTests.cs`

**Interfaces:**
- Consumes: `PrivilegedSwap` (Task 13)
- Produces: `CliAdoptionCoordinator.FinishOffered : IObservable<bool>` (true while an install is left with `PromptCancelled` or `DeletionCutShort`) and `Task FinishSwitchingAsync()`; `TrayViewModel` gains `IObservable<bool>? finishSwitchOffered` and `Func<Task>? finishSwitch`, inserted before `openLaunchApprovals`, its last parameter, behind a `FinishSwitchCommand` labelled "Finish switching to the app's kcap…"

- [ ] **Step 1: Write the failing tests**

```csharp
[Test] public async Task A_root_owned_install_runs_last_behind_one_prompt_and_is_switched()
[Test] public async Task The_prompt_text_names_what_it_removes_and_that_every_account_shares_it()
[Test] public async Task A_cancelled_prompt_leaves_the_install_and_offers_Finish_switching()
[Test] public async Task Finish_switching_reruns_the_batch_for_that_install_and_prompts_again()
[Test] public async Task A_helper_still_running_at_launch_is_waited_for_before_anything_else()
[Test] public async Task A_part_deleted_aside_after_go_is_left_DeletionCutShort_without_a_prompt()
[Test] public async Task A_manual_daemon_started_while_the_prompt_was_open_puts_everything_back()
[Test] public async Task A_left_install_the_user_removed_by_hand_is_recorded_switched_silently()
[Test] public async Task No_bare_sudo_npm_uninstall_appears_in_any_notice()
```

- [ ] **Step 2–4:** fail, implement, pass
- [ ] **Step 5: README** — the administrator prompt for an install under `/usr/local`, the shared-install note, the `~/Applications` exception and **Finish switching to the app's kcap…**
- [ ] **Step 6: Full app suite and publish check**
- [ ] **Step 7: Commit** — `git commit -m "Take over root-owned kcap installs behind one admin prompt (#2373)"`
- [ ] **Step 8: Live check** with Node's official installer (`/usr/local`), `sudo npm install -g @kurrent/kcap`, the app in `/Applications`: one prompt, `/usr/local/bin/kcap` links to the bundle, the package folders gone; then cancel the prompt and finish from the menu bar
