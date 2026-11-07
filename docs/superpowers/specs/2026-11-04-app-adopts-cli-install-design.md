# The macOS app takes over an npm or script kcap install

## Why

The desktop app bundles its own `kcap` and `kcap-daemon`. A user who installed the CLI first, with `npm install -g @kurrent/kcap` or the install script, and then installed the app, ends up with two kcaps:

- The daemon's launchd service still runs the npm or script binary. The app finds the service running at startup and leaves it alone (`DaemonLifecycleController.RunStartupMatrixAsync`: `Running` returns)
- A terminal's `kcap` is still the npm or script one. The once-ever offer to link the app's CLI bows out as soon as any `kcap` is on PATH (`ShimOfferCoordinator.RunAsync`)
- MCP registrations, the Claude plugin registration and the service unit name the old binary's absolute path, and every hook runs a bare `kcap`, which PATH resolves to the old binary

Every app update then replaces a CLI nobody runs. The daemon never moves, so the app waited on a restart that could not help (#2360). PR #2361 turned that wait into a message saying the daemon runs another install, which the user then has to update by hand, separately from the app. The user who asked for this hit exactly that on their own machine, which is the same case as any new app user who had the CLI first: reinstalling the app does not fix it, because the same startup path leaves the same service alone

The fix is one kcap per machine, and when the app is installed, that kcap is the app's

## Goals

- When the app finds the daemon service, the terminal's `kcap` or a `kcap` link belonging to an npm or script install, it moves the machine onto its bundled CLI and removes the old install, without asking
- The existing `~/.config/kcap` is used as it is: profile, tokens, daemon name, accounts. A user whose config already holds a usable sign-in never sees onboarding
- After the switch, the next app update moves the daemon by itself, and `kcap --version` in a new terminal matches the app
- It reaches users who installed the app long ago as well as new ones: it runs on whichever launch first finds another install, not only on a first run

## Non-goals

- Windows and Linux. macOS only in this change. Windows already retires its pre-rebrand install with `LegacyInstallRetirement`; the npm and script cases there can follow later
- Changing what the onboarding gate decides. It already reads the shared config and skips the wizard when a profile has a usable token. A CLI that was installed but never set up has no such config, so the wizard runs as it does today, and the switch still removes the old install afterwards
- Homebrew or any other package manager, dev builds and copied binaries (`InstallKind.Unknown`). They are never touched
- Migrating the config itself. Every kcap binary already shares `~/.config/kcap` and the fixed daemons directory

## Anti-goals

The switch has failed, even if the old install is gone, if it leaves:

- No working `kcap` on a PATH that found the old one: a new terminal's, or one a daemon service carries, which its hosted agents and their hooks inherit
- A daemon that does not come back, or comes back under another name, profile or server, or a stopped service started
- A user-scope hook, MCP registration, plugin registration or service unit that names a binary or plugin directory that no longer exists, Claude's own plugin records included
- A running session, hosted or not, whose kcap plugin or MCP server was loaded from the old install, losing those files while it still runs
- Work interrupted that an ordinary update restart of that daemon would have waited for
- The user asked to confirm the switch. They chose a silent switch. The one prompt allowed is macOS's own administrator password prompt, for an install in a root-owned folder

## What the user sees

Nothing to answer, apart from macOS's administrator password prompt when the old install sits in a root-owned folder. After a switch, the app posts one notice, for example "kcap now uses the app's command-line tool. The npm install of kcap 1.4.1-beta.2 was removed". When an install was left, the notice says why and what finishes it (see "Left installs"). A notice is posted once per outcome, never again on later launches for the same outcome

## When it runs

On every macOS launch, after the onboarding gate has resolved and the daemon graph is built, the app looks for another install, and again while it runs whenever a batch is pending (see "States and retries"). When the wizard runs, the check waits until the wizard has finished, so a service the wizard installs is already the app's

The ordinary shim offer waits for that first look to finish: `ShimOfferCoordinator` starts inside `BuildDaemonGraph`, before the look, and would otherwise confirm a link the switch is about to make. It already waits on the setup wizard's link the same way. After the look, it stays silent while anything is pending or left

The switch is skipped entirely when the app runs with `KCAP_APP_CLI_PATH` set, or from anything other than an installed bundle (the cases `InstallLocation` already distinguishes)

## What counts as another install

Evidence, each read on its own:

1. **Service units.** `~/Library/LaunchAgents/io.kurrent.kcap.daemon.*.plist` whose `ProgramArguments[0]` is a `kcap-daemon` inside an npm or script install, loaded or not
2. **A running daemon.** The executable of a daemon in the daemons directory, read from its pid as PR #2361 does
3. **The terminal's `kcap`.** What the login-shell probe (`ILoginShellProbe.KcapPathAsync`) resolves, followed through links
4. **`kcap` links** in `KcapLinkFolders.For(home)` that resolve into an npm or script install

An install is identified by the same rules `InstallProvenance` uses: `NpmInstallLayout.IsPlatformBinary` for npm, `ScriptInstallLayout.FromBinary` for a script install (`versions/<v>/bin` or `current/bin` under a root carrying `install.json`). Anything that fits neither is not ours to remove and is left alone. There can be more than one, such as npm installs under two nvm Node versions

## The switch is one batch

Everything found on a launch is switched together, as one batch, because the PATH and the integrations are machine-wide

- If any install found is newer than the app's CLI, the whole batch waits and nothing is changed: switching would downgrade a daemon or leave the newer install winning on PATH. The app checks again on each launch, without a notice, and runs the batch once the app has caught up. PR #2361's report keeps saying the daemon runs another install meanwhile
- An install that preflight leaves (see "Left installs") stays out of the batch's changes; the rest of the batch goes ahead. A PATH that finds `kcap` in a left install keeps finding it there, and the PATH check in step 4 covers only the PATHs whose `kcap` the batch replaces, so a left install that wins a PATH never holds the rest of the batch pending
- The batch is complete when every install in it has ended switched or left. Only then does the app record that this machine has had its switch

## The switch, in order

Every attempt starts from fresh evidence. What an earlier attempt recorded is used to recover what it changed, never in place of a check. Integrations and PATH move first and the daemon after them, so agents the daemon resumes when it comes back already load the app's plugin and run the app's `kcap`. A daemon move that fails or defers does not undo them: the machine then runs the app's `kcap` with the old install still on disk, which works, and the batch stays pending

1. **Preflight**, changing nothing. For each install:
   - **Who can remove it.** This user, when they can write the folders holding its files and its PATH entries, or an administrator, when those folders are root-owned (Node's official macOS installer puts npm's global packages under `/usr/local`, so such an install was made with `sudo npm install -g`)
   - **Who runs its daemon.** A daemon running from the install counts as the service's only when its pid and start identity match the loaded launchd job's. Any other daemon running from it, such as one started with `kcap daemon start` to carry a machine credential a service cannot hold, or a manual daemon beside an unloaded or stale plist, leaves the install: deleting its binary would strand that daemon on a version that never updates
   - **Where PATH finds it.** The PATHs that matter are the login shell's and each service unit's stored `PATH`, which the daemon's agents and their hooks inherit. For each, the app finds the location where `kcap` resolves. Every location that resolves into the install must be replaceable in place by a link to the app's `kcap`: a link such as npm's `<prefix>/bin/kcap` under any prefix (nvm's per-version `bin` included) or a script install's `~/.local/bin/kcap`. A PATH entry naming the install's own folder (`<root>/current/bin`) cannot be, so the install is left
2. **Record the intent** durably, before anything changes: each install's identity (its root, version and binary file identity, so a reinstall at the same path reads as a different install), each service unit's full plist and whether it was loaded and running, and the steps planned
3. **Repoint the agent integrations.** Run the bundle's `kcap refresh --app-migration`, the step `ApplicationIntegrationRefresh` already runs after a bundle move. That rewrites `settings.json` but not Claude's own plugin records (`plugins/known_marketplaces.json`, `plugins/installed_plugins.json`), which for a directory marketplace keep naming the npm package's plugin folder. Where kcap's Claude plugin is installed and its records name the old install, the switch repoints those records too, in every Claude config directory kcap knows: the default one and each registered account's. A Claude directory with no kcap plugin is left as it is; the switch never installs an integration the user did not have. The moment the last of these rewrites finishes is the batch's **cutover**, recorded durably the first time step 3 completes. A later attempt that runs step 3 again without finding anything naming the old install leaves the recorded cutover as it is; only an attempt that finds an integration naming the old install again, and repoints it, moves the cutover to its own finish
4. **Replace the PATH locations** found in preflight, each at its own path, by a link to the bundle's `kcap` renamed over the old entry. A root-owned install's locations are replaced later, in its privileged step (see "A root-owned install"). A location outside `KcapLinkFolders.For(home)` (a custom npm prefix, nvm's per-version `bin`) keeps its replacement and also gets a stable link from `PathShimInstaller`, so a shell started after a version switch, or after the app moves, still finds `kcap`. Each link made outside those folders goes into the app's link inventory before it is made (see "Links outside the usual folders"). When no PATH found `kcap` at all, as with an install only a service names, the app makes that stable link silently. Then every PATH whose `kcap` the batch replaced, and the login shell's when it found none, must resolve `kcap` to the bundle. Nothing has been deleted yet, so a failure here puts the old entries back and leaves the batch pending
5. **Move each daemon service onto the app's daemon.** Through the app's one daemon mutation lane, as one operation whose outcome the lane's shared evidence predicate classifies, not a custom action that bypasses it
   - **A unit that was not loaded** is rewritten for its next start and left unloaded. Its step counts when the plist on disk names the bundle's daemon; no `Hello` is asked of it
   - **A job loaded but not running** keeps its old command in launchd while it stays loaded: launchd does not reread a loaded job's plist (`LaunchdUnit` notes the loaded program lags the file). It cannot be reloaded without starting: kcap's units carry `RunAtLoad`, `launchctl bootstrap` has no option to load without running, and `LaunchdServiceManager.Install(startNow: false)` bootstraps and then signals the process, which this switch must not do. So the job is booted out and left unloaded with its rewritten plist, and the step counts when launchd holds no job under that label and the plist names the bundle's daemon. Rollback restores the recorded plist and also leaves it unloaded. Unloaded and loaded-but-stopped are the same stopped state to the app: its startup matrix starts a present unit in either state (`StartVerified`), so the user sees no difference beyond the bundle's daemon running at that next start
   - **Wait for a safe point, as an update restart would.** The app waits until the daemon reports no turn in flight and no other work it can see, treating any signal it cannot read as busy, and only then asks it to stop keeping its agents, with `WhenIdle` set and `AtTurnBoundary` not set. The daemon then checks for an evaluation or a launch in admission and either refuses (`busy:`) or stops within the same request, so no hard-busy work can begin between its check and the stop. The daemon's own turn-boundary hold (`shutdown/2`) is not used: once its turn wait runs out it stops without checking for hard-busy work again, which an update restart never does. A `busy:` refusal defers the switch. A daemon that predates `WhenIdle` stops at once, so for it the app's own observation is the only gate. A daemon older than the keep-agents handshake (1.3.1) cannot keep its agents across any restart: its agents end when it stops, and the notice says so. While deferred, the batch stays pending without repeated notices
   - **Check again right before the stop.** A long wait can outlast what preflight saw: an npm update can put a newer daemon in place. The install's identity and version, and the job's pid, must still be what preflight recorded
   - **Rewrite only `ProgramArguments[0]`** to the bundle's `kcap-daemon`, keeping every other key, the stored environment included, then bootstrap the job again
   - **Prove the successor.** The step counts only when a new instance runs: a pid and start identity different from the old one, the bundle's executable, matching the loaded launchd job, answering `Hello` with the expected name, profile and server. An old instance still answering `Hello` is not success
   - **Roll back on failure** to the recorded plist and the recorded state, running or not, and verify that state. A launch that finds the intent recorded but the app gone mid-step recovers from the recorded plist the same way
6. **Check again, immediately before deleting.** Fresh evidence that:
   - the install's identity is unchanged
   - no process runs an executable inside it: no daemon, MCP server or watcher
   - no agent harness process (Claude Code, Codex, Cursor and the other harnesses kcap integrates with) is still running that started before the recorded cutover. Such a session read its plugin, hooks and MCP commands when it started, and may still read the old plugin folder or relaunch an MCP server from the old path, even with no kcap process of its own running: plugin MCP servers can be disabled or exit, and a session excluded from capture runs no watcher. A session that started while step 3 was still rewriting counts as before the cutover. Agents the app's daemon resumed after step 5 started after the cutover, so they do not hold the deletion up, however many times a pending batch is checked again
   - nothing names it: service units and launchd's loaded definitions, the Codex and other harness hook files, MCP registrations, the git hook, and Claude's plugin records wherever kcap's Claude plugin is installed. When `ClaudePluginInstaller.EffectiveMcpJsonPath` resolves, it must resolve into the bundle. When it resolves nothing because Claude has not yet written an install record for a plugin `settings.json` enables, the check passes once `settings.json` names the bundle and no record names the old install: the outcome must not depend on the user opening Claude between attempts. A Claude directory with no kcap plugin passes; one whose records cannot be read does not
   The deletion waits silently for any of these, and is checked again while the app runs and on later launches
   Anything still running from it or naming it keeps the install, and the batch stays pending
7. **Delete the old files.** For each folder, the app first records the aside name it will use, then renames the folder to it, checks the renamed tree is still the recorded install, and only then deletes it, as `ApplicationRetirement` does for a bundle: a crash at any point leaves a record naming where the files are. npm: its package folders under `<prefix>/lib/node_modules/@kurrent/`, the same tree `npm uninstall -g` deletes, which needs neither npm nor node to be found, each recorded and moved on its own. Script: `versions`, `current` and `install.json` under the root, not the whole root, which can hold data the installer does not own (`UninstallCommand.KeptProgramLine` gives the same rule)
8. **Record it switched.** The shim offer is recorded as made, so its dialog never appears later

### A root-owned install

Its PATH replacements and deletion need administrator rights, so they run after everything else, behind a single macOS administrator prompt, through the `osascript … with administrator privileges` route `ApplicationReferences.ReplaceLinkAsync` already takes for a root-owned link. That route starts a separate process per call and keeps no authorization between calls, so the one prompt starts one privileged process that stays alive until the install is deleted or put back, taking each next step only on the app's word. Moving the folders under a parent this user owns would not make them deletable without it: a rename keeps a root-owned tree's ownership and modes. Once the password is given:

1. The privileged process replaces the install's PATH links. The package folders stay where they are, so a session that starts while the prompt is open still finds every file
2. The app, as the user, verifies the PATHs as step 4 does and repeats step 6's checks: identity, processes, harness sessions and references, any of which may have changed while the prompt was open
3. On the app's go-ahead, the privileged process records nothing itself but follows step 7: renames each folder aside under the name the app recorded, checks it is still the recorded install, and deletes it. When a check failed, it puts the links back instead, and the install stays pending

The privileged process's interface is a go or a put-back for one recorded install, nothing broader. The paths it touches are fixed when it starts, from the app's record of that install; the channel from the app carries only the word go or put-back, so nothing the channel says can widen what it touches

Its lifecycle, so it never outlives what it was started for:

- **Before go**, put-back is its default. If the app quits or crashes, the control connection closes, or no word arrives within a bounded wait, it puts the links back and exits
- **After go**, the deletion is committed. It finishes the renames and deletions even if the app goes away, then exits
- **The app records** the helper's pid and start identity when it starts it, and whether go was sent. A later launch settles that operation before starting another: it waits for a helper that is still that process to exit, then reads the filesystem afresh. Links that point at the bundle with the install still present are a working state, and the batch stays pending. Aside folders a helper left part-deleted can only be removed with administrator rights again, so the install is recorded as left, with **Finish switching…** to finish it; the app does not raise a second prompt on its own

A root-owned install serves the whole Mac, and the app cannot see other accounts' services or hooks, whose Library folders it cannot read. Most Macs running kcap have one user, so the switch still deletes it, and the prompt's text says what it removes and that every account on this Mac shares it, so the administrator typing the password knows another account may lose its `kcap`. Another account's service that named the deleted daemon fails at its next start, and that account's own app, once installed, repairs it

When the app runs from `~/Applications` rather than `/Applications`, a root-owned install is left: replacing a machine-wide `/usr/local/bin/kcap` with a link into one user's home folder would hand every other account a command it may not be able to run

## Links outside the usual folders

A link the switch puts in a custom npm prefix or an nvm `bin` is in a folder nothing else in kcap looks at: `ApplicationReferences` (a later move between bundle paths), `kcap uninstall` and `MacAppRemoval` all walk `KcapLinkFolders.For(home)` only. The switch records each such link in config before it makes it, so the inventory holds it while the batch is still pending, which can last for days while old sessions run. All three read the inventory, pending entries included, with their existing target and ownership checks before they touch an entry. Without it, a bundle move leaves those links pointing at the old bundle, and an uninstall leaves them dangling

## States and retries

The app keeps a record in config beside `app-integration-refresh-pending`, written the way `ApplicationIntegrationRefresh` and `ApplicationRetirement` write theirs:

- **Pending.** The batch started and has not finished: deferred for a daemon's safe point, waiting for processes still running from an old install, or interrupted. Checked again while the app runs and on the next launch, from fresh evidence, without repeated notices
- **Waiting.** An install newer than the app was found, so nothing has changed. Checked again on each launch, without a notice
- **Left**, per install. It could not be removed for a reason only the user can fix (see below). Reported once. An install left because its own folder is on PATH, or because a daemon its service does not run is running from it, is checked again on each launch without a notice, since that condition ends when the user fixes it; the other reasons wait for **Finish switching…**
- **Switched**, per install

After the machine's batch completes, an npm or script install that appears later, for example one the user put back on purpose, is reported but not removed

## Left installs

An install is left when its PATH entry is its own folder, a daemon not run by its service runs from it, the user cancelled the administrator prompt, a privileged deletion was cut short, or it is root-owned while the app runs from `~/Applications`. The notice names the install, says why, and offers **Finish switching to the app's kcap…** in the menu bar menu, which runs the same batch for that install and asks for the password again only because the user chose it. The app never hands out `sudo npm uninstall -g @kurrent/kcap` alone, since running it before a replacement exists would leave no `kcap` on that PATH

A left install that has since disappeared, with every PATH from preflight resolving `kcap` to the bundle, is recorded switched without a notice: the user finished it by hand

## Logging

Each step writes an event to `app-lifecycle.jsonl`, named and shaped like the `legacy_*` events `LegacyInstallRetirement` writes: what it found, each step's result, what it kept and why

## Constraints from the codebase

- All daemon mutations go through the app's one lane, with its shared evidence predicate. A step that cannot prove it succeeded is a failure, never a success
- The daemon is stopped only through its shutdown handshake and launchd, never by signalling its process
- `ApplicationReferences` keeps its plist preimages and progress flags in memory only, which is enough for a bundle move inside one process but not for this switch: the preimages and the step reached must be on disk before a job is stopped
- `daemon service install --replace --verify` is not a substitute for step 5: it captures a new environment from the invoking process instead of keeping the unit's, and its rollback removes the new unit rather than restoring the old one and its state

## Known gap

`kcap plugin install --project` writes the old plugin directory's absolute path into `<repo>/.claude/settings.local.json`. The app cannot list repositories, so a project-scope install keeps naming a deleted directory until `kcap plugin install --project` is run again in that repository. The README's description of the switch says so

## Tests the change needs

- PATH continuity: an nvm install, a custom npm prefix, a script install reached through `<root>/current/bin` on PATH, a service unit whose PATH lacks `~/.local/bin`, a failing shim or profile write, an install only a service names with no `kcap` on any PATH (it ends with a working `kcap` and no dialog, with the first look deliberately delayed). A fresh login shell resolving to the bundle is not enough on its own for any of them
- Daemon ownership: an unloaded plist beside a manual daemon, a job pid that does not match the running daemon, an old instance still answering `Hello`
- Inactive services: an unloaded unit and a loaded but stopped one, with no manual daemon, spawn no daemon process during the switch or its rollback, and both end unloaded; after cleanup, starting each runs the bundle's daemon
- Safe point: a switch during an evaluation, during a launch whose resume entry is not yet durable, during an interactive turn, with a non-resumable flow participant, an evaluation or launch beginning between the app's observation and the stop (refused `busy:`, switch deferred), a daemon that predates `WhenIdle`, and an npm update landing during the wait
- Claude plugin records: the default and a registered account's config directory, legacy marketplace names, a directory marketplace whose cache path does not exist, a machine with no Claude plugin, an account without the plugin, and a plugin enabled in `settings.json` with no install record yet, which completes without Claude being opened
- Live consumers: a hosted Claude agent resumed during the switch reads a plugin skill and relaunches an MCP server after cleanup; a Claude session started before the switch, with no kcap MCP server or watcher running, keeps the old files until it ends and reads a plugin skill late in its life
- Pending batches: a batch held pending by an old session, then a bundle move or an uninstall before cleanup, with a custom-prefix and an nvm link
- Cutover: a harness session starting midway through step 3; repeated pending checks with a hosted agent the app's daemon resumed still running, which never holds the deletion
- Crash and relaunch: between stopping the job and rewriting it, between rewriting and bootstrapping, right after a rename and before the next record write, between two npm package moves; references recreated between two launches; an install reinstalled at the same path between launches
- Batches: an older install beside a newer one, pending beside waiting, several installs found at once, a left install that wins the login PATH beside an eligible one, a cancelled prompt in a batch of several
- Root-owned: a manual daemon started, an integration restored, or a session started that reads a plugin file, while the password prompt is open, with every file reachable throughout; a PATH check failing after the privileged link swap; a genuinely root-owned package tree with nested directories the user cannot write, deleted after one prompt; the app quitting or crashing after the link swap, while the helper awaits go, and during deletion, each ending with the helper gone and the files in a state the next launch settles
- Left installs: a cancelled prompt followed by **Finish switching…**, and an install the user removed by hand
- Shared installs: a root-owned install with the app in `/Applications` (switched, prompt text names the sharing) and in `~/Applications` (left)
- Adopted links: the switch, then a bundle move, then an uninstall, for a custom npm prefix and an nvm link

## Related work

- #2360 / PR #2361, merged, detects a daemon running another install and reports it after an update. This change consumes the same evidence and, on macOS, makes that report rare: it remains while a batch waits or an install is left, and for an install that came back after the switch. The implementation branches from a `main` that includes it. Neither turn-boundary capability fits this switch: `shutdown/2` (#2358) does not protect hard-busy work after its turn wait, and #2361's `restart/2` relaunches the job launchd already holds, which still names the old binary
- `LaunchdServiceManager.RefreshUnit` already rewrites only a unit's binary, stops a running daemon with the keep-agents stop that refuses while busy, holds the service lock, and restores the original plist when the bootstrap fails. `kcap daemon service repoint --from <dir>`, which the Windows legacy cleanup runs through the same contract, gains a launchd branch on top of it for step 5, and the app runs it through the lane like its other service verbs
- `ApplicationReferences` moves service units and links between two app bundles; step 4's link swap is its `ReplaceLinkAsync`

## Left to the builder

- The record's file name and format
- The notice's exact wording, and the administrator prompt's text
- How the reference check in step 6 is shared with `kcap uninstall`, which already finds most of these sites
- How Claude's plugin records are repointed: a CLI step the refresh runs, or the app writing them, as long as `EffectiveMcpJsonPath` resolves into the bundle afterwards
- How often a pending batch is checked again while the app runs
