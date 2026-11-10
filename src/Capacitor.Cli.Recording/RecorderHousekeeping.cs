using System.Text;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Claude;
using Capacitor.Cli.Core.Harness.Codex;
using Capacitor.Cli.Core.Harness.Grok;
using Capacitor.Cli.Core.Harness.Muse;
using Capacitor.Cli.Core.Harness.Titles;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.RepoEvidence;
using Capacitor.Cli.Core.Workspace;
using Capacitor.Cli.Recording.Harness.Antigravity;
using Capacitor.Cli.Recording.Harness.Codex;
using Capacitor.Cli.Recording.Harness.Gemini;
using Capacitor.Cli.Recording.Harness.Grok;
using Capacitor.Cli.Recording.Harness.MistralVibe;
using Capacitor.Cli.Recording.Harness.Muse;
using Capacitor.Cli.Recording.Harness.OpenCode;
using Capacitor.Cli.Recording.Harness.Titles;
using Capacitor.Cli.Recording.PrDetection;

namespace Capacitor.Cli.Recording;

/// <summary>The work between a recorder's drains that isn't itself recording: repository and pull request
/// detection, harness titles and subagent scans. One instance belongs to one recorder and keeps that recorder's
/// state. What only the process hosting the recorder can do goes through <see cref="IRecorderHost"/>.</summary>
public sealed class RecorderHousekeeping(
        RecordingSource source, bool skipTitle, int? parentPid, IRecorderHost host,
        ConfigRoot config, ProfileContext profiles, HarnessRegistry harnesses, ICapacitorHttpClient http,
        GitProviderRouter router, TimeProvider time, AccountStore accounts, UserHome home,
        Action<string> unguardedLog, Action unguardedBeat) : IRecorderHousekeeping {
    // These reach here apart from the recorder's context, so its host's guards don't cover them.
    readonly Action<string> _log  = RecorderHost.Guarded(unguardedLog);
    readonly Action         _beat = RecorderHost.Guarded(unguardedBeat);

    readonly WatcherPaths        _paths = WatcherPaths.FromEnvironment(config);
    readonly SubagentScanContext _scans = new(host, config, profiles, http, time, RecorderHost.Guarded(unguardedLog));

    IHarnessTitleStore? _titleStore;

    readonly List<MistralVibeSubagent> _vibeSubagents = [];
    readonly GrokSubagentTracker       _grokSubagents = new();
    readonly MuseChildWatchers         _museChildren  = new();

    // What each refresh last read of a Vibe store, so an unchanged store costs a stat and not a sync.
    readonly Dictionary<string, (long Size, long Ticks)> _vibeLastSynced = new(StringComparer.Ordinal);

    // Gemini fires no subagent hooks, so the parent discovers nested subagent transcripts itself and
    // spawns a child watcher per subagent. Tracks the files already registered and spawned, so each is
    // handled exactly once across ticks.
    readonly HashSet<string> _seenSubagents = new(StringComparer.Ordinal);

    // Rollouts in the shared Codex sessions tree whose header proved they are not this session's
    // subagents. Keeps the per-tick scan to one header read per new file; a mid-write header is never
    // cached, so it is retried.
    readonly HashSet<string> _codexRuledOutRollouts = new(StringComparer.Ordinal);

    // The last mtime ChildRolloutMtimes.Advanced fired for, per child rollout: backs the re-ensure that
    // respawns a reaped child watcher when its subagent re-engages.
    readonly Dictionary<string, DateTime> _codexRolloutMtimes = new(StringComparer.Ordinal);

    // The parent is the only process that knows these exist, so its teardown must stop them.
    readonly HashSet<string> _spawnedChildWatcherKeys = new(StringComparer.Ordinal);

    DateTimeOffset _nextWorkspaceDrain = DateTimeOffset.MinValue;

    // The end the Muse SessionEnd hook handed over, posted after the final drain in place of the
    // parent-exit synthesis.
    MuseSessionEndRequest? _museEndRequest;

    // Idle grace between a Codex collab child's task_complete and its live subagent-stop post. The
    // agent type is read lazily from the child rollout's own header, once.
    TimeSpan       _codexSubagentStopGrace;
    string?        _codexChildAgentType;
    DateTimeOffset _codexStopNextAttemptAt = DateTimeOffset.MinValue;

    string Url => profiles.Resolution.ServerUrl!;

    /// <inheritdoc/>
    public IReadOnlyCollection<string> SpawnedChildKeys => _spawnedChildWatcherKeys;

    /// <summary>The end a Muse SessionEnd hook handed over, for the host to post after the final drain.</summary>
    public MuseSessionEndRequest? HandedMuseEnd => _museEndRequest;

    public async Task PrepareAsync(WatchState state, CancellationToken ct) {
        var (sessionId, agentId, transcriptPath, vendor, cwd) = source;

        _titleStore = HarnessTitleStores.For(vendor, agentId, sessionId, transcriptPath, harnesses, host, accounts.TryLoad(), home);
        if (vendor == "codex" && CodexRolloutReadings.For(accounts, transcriptPath, home) is { } codexReadings) state.ObserveCodexReadings = lines => codexReadings.Observe(lines);
        if (_titleStore is not null) state.TitleTracker = new HarnessTitleTracker(_titleStore.RecordsChangeTime);

        state.CopilotHostMissing = vendor == "copilot" && agentId is null && parentPid is null;
        _codexSubagentStopGrace  = CodexSubagentTurnTracker.ResolveStopGrace(Environment.GetEnvironmentVariable("KCAP_CODEX_SUBAGENT_IDLE_MINUTES"));

        if (skipTitle) {
            state.TitleGenerated = true;
        }

        // Detect repository info upfront if cwd is provided (session watchers only, not agents)
        if (cwd is not null) {
            state.Repository        = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time);
            state.LastRepoDetection = time.GetUtcNow();
        }

        state.Commits           = await host.ObserveCommitsAsync(sessionId, agentId, cwd);
        state.LastCoverageCheck = time.GetUtcNow();

        if (vendor == "claude" && agentId is null) {
            state.SecondaryRoots    = new SecondaryRepoRoots(GitRepository.FindRoot, cwd is null ? null : GitRepository.FindRoot(cwd));
            state.SubagentCheckouts = new ClaudeSubagentCheckouts(transcriptPath);
        }

        // No cwd-derived repo (launched outside any checkout, or no cwd at all): fall back to scanning the
        // transcript's own tool-use paths for a git root. Session watchers only, matching the cwd detection
        // above: a subagent watcher is always spawned with no cwd and never had its own repo detection.
        if (vendor == "claude" && agentId is null && (cwd is null || GitRepository.FindRoot(cwd) is null)) {
            state.EvidenceScanner = new RepoEvidenceScanner<RepositoryPayload>(
                GitRepository.FindRoot, root => RepositoryDetection.DetectRepositoryAsync(router, config, root, time),
                p => p.Owner is not null && p.RepoName is not null);

            try {
                foreach (var line in File.ReadLinesShared(transcriptPath)) {
                    if (await state.EvidenceScanner.OnLineAsync(vendor, line) is { } repo) {
                        TranscriptRecorder.ApplyEvidenceRepo(state, repo);

                        break;
                    }
                }
            } catch {
                // fail-open: this one-shot prefix scan is best-effort and must never block startup
            }
        }
    }

    public void OnConnected() => host.ArmProcessWatches();

    // Vibe replaces journal segments as it publishes, so a refresh can race a file away; the next drain
    // retries it.
    public void RefreshTranscript(string transcriptPath) {
        if (source.Vendor != "mistral-vibe") return;

        try { _vibeSubagents.AddRange(MistralVibeLiveTranscript.Refresh(transcriptPath, _vibeLastSynced)); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    public async Task BeforeDrainAsync(WatchState state, CancellationToken ct) {
        var (sessionId, agentId, _, vendor, cwd) = source;

        // Periodically refresh repository info (every 60s)
        if (cwd is not null && time.GetUtcNow() - state.LastRepoDetection > TimeSpan.FromSeconds(60)) {
            var detected = await RepositoryDetection.DetectRepositoryAsync(router, config, cwd, time);

            // An evidence-derived repo may only be replaced by another real detection,
            // never cleared back to null by a launch-cwd probe that still finds nothing.
            if (ShouldReplaceRepository(detected, state.RepositoryFromEvidence)) {
                state.Repository = detected;
            }

            state.LastRepoDetection = time.GetUtcNow();
        }

        if (state.Commits is not CommitObservation.Uncovered && time.GetUtcNow() - state.LastCoverageCheck > TimeSpan.FromSeconds(60)) {
            state.Commits           = state.Commits.Rechecked(await host.CoversCommitsAsync(sessionId, cwd));
            state.LastCoverageCheck = time.GetUtcNow();
        }

        if (time.GetUtcNow() - state.LastSubagentCheckoutScan >= SubagentCheckoutScanGap) {
            ObserveSubagentCheckouts(state, _log);
            state.LastSubagentCheckoutScan = time.GetUtcNow();
        }

        if (SecondaryProbeDue(state, time.GetUtcNow())) {
            await LinkSecondaryPullRequestsAsync(state, sessionId, SecondaryProbeBudget, ct, _beat);
            state.LastSecondaryProbe = time.GetUtcNow();
        }

        if (state.PendingCreatedPullRequests.Count > 0 && time.GetUtcNow() >= state.NextCreatedPullRequestLink) {
            await LinkCreatedPullRequestsAsync(state, sessionId, SecondaryProbeBudget, ct, lastChance: false, _beat);
            // Only a PR left pending waits; one created after a clean pass goes at once.
            state.NextCreatedPullRequestLink = state.PendingCreatedPullRequests.Count > 0 ? time.GetUtcNow() + TimeSpan.FromSeconds(60) : default;
        }

        // Before the first drain, so a transcript title line can be judged by the time LLM titling is due.
        if (agentId is null && TranscriptTitleLines.MayNeedHarnessTitles(vendor) && state.ServerRecordsHarnessTitles is null
         && time.GetUtcNow() - state.LastHarnessTitleProbe >= HarnessTitleRetryGap) {
            state.LastHarnessTitleProbe      = time.GetUtcNow();
            state.ServerRecordsHarnessTitles = await ProbeHarnessTitlesAsync(ct, _beat);
        }

        // Read once before the first drain, so a store title the server records latches HarnessTitleSeen
        // before that drain can trigger LLM titling.
        if (_titleStore is not null && state.LastHarnessTitleRead == default) {
            state.LastHarnessTitleRead = time.GetUtcNow();
            await PostHarnessTitleAsync(_titleStore, sessionId, state, ct, _beat);
        }
    }

    public async Task AfterDrainAsync(WatchState state, string transcriptPath, IReadOnlyList<string> drained, CancellationToken ct) {
        var (sessionId, agentId, _, vendor, _) = source;

        // Also polled on its own cadence: some stores (Cursor's meta.json, Antigravity's sqlite row) change
        // while the transcript stays idle.
        if (_titleStore is not null && (drained.Count > 0 || time.GetUtcNow() - state.LastHarnessTitleRead >= HarnessTitleRetryGap)) {
            state.LastHarnessTitleRead = time.GetUtcNow();
            await PostHarnessTitleAsync(_titleStore, sessionId, state, ct, _beat);
        }

        // Live subagent discovery: only the parent watcher scans; child watchers just stream their file.
        // Gemini scans its native nested chat files; OpenCode scans the nested dir the kcap plugin writes
        // child {info,parts} into; Codex scans the shared sessions tree for collab rollouts whose
        // session_meta names this session as parent_thread_id; Antigravity links subagents from the
        // INVOKE_SUBAGENT steps drained this tick, nesting only, since subagents are captured standalone.
        if (agentId is null && vendor == "gemini") {
            await GeminiSubagentScan.ScanGeminiSubagents(_scans, sessionId, transcriptPath, _seenSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "opencode") {
            await OpenCodeSubagentScan.ScanOpenCodeSubagents(_scans, sessionId, transcriptPath, _seenSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "codex") {
            await CodexSubagentScan.ScanCodexSubagents(_scans, sessionId, transcriptPath, _seenSubagents, _codexRuledOutRollouts,
                _codexRolloutMtimes, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "antigravity") {
            await AntigravitySubagentLinks.ScanAntigravitySubagentLinks(_scans, sessionId, drained, state.PostedSubagentLinks, ct);
        } else if (agentId is null && vendor == "mistral-vibe") {
            await MistralVibeSubagentScan.ScanMistralVibeSubagents(_scans, sessionId, transcriptPath, _vibeSubagents, _seenSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "grok") {
            await GrokSubagentScan.ScanGrokSubagents(_scans, sessionId, transcriptPath, drained, _grokSubagents, _spawnedChildWatcherKeys, ct);
        } else if (agentId is null && vendor == "muse") {
            await MuseSubagentScan.ScanMuseSubagents(_scans, sessionId, transcriptPath, _seenSubagents, _museChildren, _spawnedChildWatcherKeys, ct);
            await MuseSubagentScan.StopFinishedMuseChildren(_scans, sessionId, transcriptPath, drained, _museChildren);

            if (MuseSessionEndRequest.ClaimHandedEnd(_paths, sessionId, Url, time) is { } handed) {
                _log("Muse SessionEnd hook handed its session-end to this watcher; draining and posting it");
                _museEndRequest = handed;
                host.StopRun();
            }
        }

        // A Codex collab child posts its own subagent-stop once its rollout's last turn completed and the
        // grace elapsed; otherwise the parent's end is the only stop, and a finished child's card spins for
        // the parent's whole lifetime. The server dedupes lifecycle events, so the tracker latches after the
        // first success, and a failed post retries on its own interval. The child keeps streaming after the
        // stop, since appends are not lifecycle-gated.
        if (agentId is not null && vendor == "codex"
         && time.GetUtcNow() >= _codexStopNextAttemptAt
         && state.CodexSubagentTurn.ShouldPostStop(
                state.PendingCodexToolCalls.Count, state.LastActivityAt,
                time.GetUtcNow(), _codexSubagentStopGrace)) {
            _codexChildAgentType ??= CodexSubagentScan.ResolveCodexChildAgentType(transcriptPath);

            if (await CodexSubagentScan.PostCodexSubagentStopAsync(_scans, sessionId, agentId, _codexChildAgentType, transcriptPath, ct)) {
                state.CodexSubagentTurn.StopPosted = true;
                _log($"Codex subagent {agentId} ({_codexChildAgentType}) turn complete + idle "
                  + $"{_codexSubagentStopGrace.TotalMinutes:F0}m; posted subagent-stop");
            } else {
                _codexStopNextAttemptAt = time.GetUtcNow() + CodexSubagentScan.CodexSubagentStopRetryInterval;
            }
        }

        // Codex reads a hook's answer only when its process exits, so its hooks never post a workspace span, and a
        // session may run no other hook: the session's recorder delivers the workspace spool too.
        if (agentId is null && time.GetUtcNow() >= _nextWorkspaceDrain) {
            _nextWorkspaceDrain = time.GetUtcNow() + WorkspaceSpool.DrainThrottle;
            await host.DrainWorkspaceAsync();
        }
    }

    public async Task AfterFinalDrainAsync(WatchState state, IReadOnlyList<string> drained, long shutdownStarted, CancellationToken ct) {
        var (sessionId, agentId, _, vendor, _) = source;

        // The parent may have emitted an INVOKE_SUBAGENT step after the loop's last tick, and this is the
        // last chance to link it.
        if (agentId is null && vendor == "antigravity") {
            await AntigravitySubagentLinks.ScanAntigravitySubagentLinks(_scans, sessionId, drained, state.PostedSubagentLinks, ct);
        }

        // A store the transcript does not carry can change after the loop's last read; this is its last chance.
        if (_titleStore is not null) {
            await PostHarnessTitleAsync(_titleStore, sessionId, state, ct, _beat,
                FinalSecondaryProbeDeadline - time.GetElapsedTime(shutdownStarted));
        }

        // A PR is usually opened in the session's last turn, after the previous 60s probe.
        await LinkCreatedPullRequestsAsync(state, sessionId,
            FinalSecondaryProbeDeadline - time.GetElapsedTime(shutdownStarted), ct, lastChance: true, _beat);
        ObserveSubagentCheckouts(state, _log, final: true);
        await LinkSecondaryPullRequestsAsync(state, sessionId,
            FinalSecondaryProbeDeadline - time.GetElapsedTime(shutdownStarted), ct, _beat);
    }

    public IReadOnlyList<(long Idx, string Line)> ReadAntigravityUsage(string dbPath, long afterIdx, string? createdAt) =>
        host.ReadAntigravityUsage(dbPath, afterIdx, createdAt);

    public Task<ClaudeCliResult?> GenerateTitleAsync(string userText, string? assistantText, CancellationToken ct) =>
        TitleGeneration.GenerateAsync(userText, assistantText, time, _log, profiles.Resolution.Profile, harnesses, config, source.Vendor, ct);

    public void HandOffTitle(string transcriptPath) => host.HandOffTitle(source.SessionId, source.Vendor, transcriptPath);

    /// <summary>
    /// The first probe waits for a root rather than spending its minute on none, since a short session can
    /// end before the next one and the final probe is skipped below the transcript threshold.
    /// </summary>
    internal static bool SecondaryProbeDue(WatchState state, DateTimeOffset now) =>
        state.SecondaryRoots is { Roots.Count: > 0 } && now - state.LastSecondaryProbe > TimeSpan.FromSeconds(60);

    static readonly TimeSpan SubagentCheckoutScanGap = TimeSpan.FromSeconds(10);

    internal static void ObserveSubagentCheckouts(WatchState state, Action<string> log, bool final = false) {
        if (state is not { SubagentCheckouts: { } subagentCheckouts, SecondaryRoots: { } secondaryRoots }) return;

        var knownRoots = secondaryRoots.Roots.Count;
        subagentCheckouts.FeedInto(secondaryRoots, final);
        if (secondaryRoots.Roots.Count > knownRoots) log($"Probing secondary checkouts for a PR: {string.Join(", ", secondaryRoots.Roots)}");
    }

    // An evidence-derived repo (from RepoEvidenceScanner, for a session launched outside any
    // checkout) has no cwd to re-probe, so a periodic cwd-based refresh finding nothing must
    // never be allowed to clear it back to null.
    internal static bool ShouldReplaceRepository(RepositoryPayload? detected, bool repositoryFromEvidence) =>
        detected is not null || !repositoryFromEvidence;

    /// <summary>
    /// Probes every checkout the agent mutated outside its launch cwd and links each PR found
    /// there to the session. The PR is linked under the repository its URL names, not the
    /// checkout's origin: in a fork checkout <c>gh pr view</c> resolves the base repository's PR
    /// while origin names the fork. A PR is posted once per (owner, repo, number); a failed post
    /// is retried on the next pass. Only github.com PRs are linked: the server endpoint rebuilds
    /// the remote URL from owner and repo on github.com, so any other host would hash to the
    /// wrong repository. Detection takes no cancellation token, so a cancelled pass abandons an
    /// in-flight probe (its child process dies on its own cap) rather than waiting for it. Each
    /// pass starts after the root the previous one last attempted, so a slow root cannot shadow
    /// the roots behind it every time, and <paramref name="beat"/> is called before every probe
    /// because the caller's heartbeat is otherwise untouched for the whole pass.
    /// </summary>
    internal static async Task LinkSecondaryPullRequestsAsync(
            WatchState                                          state,
            Func<string, TimeSpan, Task<RepositoryPayload?>>    detect,
            Func<RepositoryPayload, CancellationToken, Task<bool>> post,
            TimeSpan                                            budget,
            TimeProvider                                        time,
            CancellationToken                                   ct,
            Action                                              beat
        ) {
        if (state.SecondaryRoots is null || budget <= TimeSpan.Zero) return;

        var roots = state.SecondaryRoots.Roots.ToArray();
        if (roots.Length == 0) return;

        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(budget, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        var first = Math.Max(0, Array.IndexOf(roots, state.NextSecondaryRoot));

        for (var i = 0; i < roots.Length; i++) {
            var remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || linked.IsCancellationRequested) return;

            // Advanced only once this root is actually attempted, so a root the check above
            // skips is the one the next pass starts with.
            var root = roots[(first + i) % roots.Length];
            state.NextSecondaryRoot = roots[(first + i + 1) % roots.Length];

            beat();

            RepositoryPayload? repo;
            try { repo = await detect(root, remaining).WaitAsync(linked.Token); }
            catch (OperationCanceledException) { return; }
            catch { repo = null; }

            if (repo is not { PrNumber: { } number, PrUrl: { } url }) continue;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !string.Equals(uri.IdnHost, "github.com", StringComparison.OrdinalIgnoreCase)) continue;
            if (!PrRefParser.TryParse(url, out var owner, out var name, out var urlNumber) || urlNumber != number) continue;

            var key = (owner, name, number);
            if (state.LinkedPullRequests.Contains(key)) continue;

            bool accepted;
            try { accepted = await post(repo with { Owner = owner, RepoName = name }, linked.Token); } catch (OperationCanceledException) { return; }
            if (accepted) state.LinkedPullRequests.Add(key);
        }
    }

    Task LinkSecondaryPullRequestsAsync(WatchState state, string sessionId, TimeSpan budget, CancellationToken ct, Action beat) =>
        LinkSecondaryPullRequestsAsync(state,
            (root, remaining) => RepositoryDetection.DetectRepositoryAsync(router, config, root, time, remaining),
            (pr, token) => PostLinkedPullRequestAsync(sessionId, pr, "from a secondary checkout", token),
            budget, time, ct, beat);

    static readonly TimeSpan MaxCreatedPullRequestLookup = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Posts each created PR the server has not accepted yet, with its title and head from gh. A PR gh cannot
    /// describe waits for a later pass, since the server records a link without a branch as one on main; on the
    /// <paramref name="lastChance"/> pass it is linked by number and URL alone. The lookup gets at most half
    /// of what is left, so the post always has time, and a PR that fails moves behind the rest.
    /// </summary>
    internal static async Task LinkCreatedPullRequestsAsync(
            WatchState                                             state,
            Func<CreatedPullRequest, TimeSpan, Task<PrInfo?>>      describe,
            Func<RepositoryPayload, CancellationToken, Task<bool>> post,
            TimeSpan                                               budget,
            TimeProvider                                           time,
            CancellationToken                                      ct,
            bool                                                   lastChance,
            Action                                                 beat
        ) {
        if (state.PendingCreatedPullRequests.Count == 0 || budget <= TimeSpan.Zero) return;

        var started = time.GetTimestamp();
        using var deadline = new CancellationTokenSource(budget, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        void MoveToBack(CreatedPullRequest pr) {
            state.PendingCreatedPullRequests.Remove(pr);
            state.PendingCreatedPullRequests.Add(pr);
        }

        foreach (var created in state.PendingCreatedPullRequests.ToArray()) {
            var remaining = budget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero || linked.IsCancellationRequested) return;

            beat();

            var key = (created.Owner, created.RepoName, created.Number);

            if (!state.LinkedPullRequests.Contains(key)) {
                var lookup = remaining / 2 < MaxCreatedPullRequestLookup ? remaining / 2 : MaxCreatedPullRequestLookup;

                PrInfo? info;
                try { info = await describe(created, lookup).WaitAsync(lookup, time, linked.Token); }
                catch (OperationCanceledException) when (linked.IsCancellationRequested) { MoveToBack(created); return; }
                catch { info = null; }

                if (info is null && !lastChance) {
                    MoveToBack(created);
                    continue;
                }

                var payload = new RepositoryPayload {
                    Host      = "github.com",
                    Owner     = created.Owner,
                    RepoName  = created.RepoName,
                    Branch    = info?.HeadRef,
                    PrNumber  = created.Number,
                    PrTitle   = info?.Title,
                    PrUrl     = created.Url,
                    PrHeadRef = info?.HeadRef,
                };

                bool accepted;
                try { accepted = await post(payload, linked.Token); } catch (OperationCanceledException) { MoveToBack(created); return; }

                if (!accepted) {
                    MoveToBack(created);
                    continue;
                }

                state.LinkedPullRequests.Add(key);
            }

            state.PendingCreatedPullRequests.Remove(created);
        }
    }

    Task LinkCreatedPullRequestsAsync(WatchState state, string sessionId, TimeSpan budget, CancellationToken ct, bool lastChance, Action beat) =>
        LinkCreatedPullRequestsAsync(state,
            (pr, lookup) => GitHubPrDetector.DescribeAsync(pr.Url, pr.Number, home.Path, lookup, RepositoryDetection.DefaultRunner(time)),
            (pr, token) => PostLinkedPullRequestAsync(sessionId, pr, "created in the session", token),
            budget, time, ct, lastChance, beat);

    // Half of WatcherHeartbeat.Threshold: the heartbeat is touched before each probe, so one
    // probe bounded by this budget is the longest the pass can leave it untouched. The pass is
    // retried every minute, so a lost post costs a minute, not the PR.
    static readonly TimeSpan SecondaryProbeBudget = TimeSpan.FromSeconds(10);

    // The watcher is killed 5s after it is told to stop. The final-line wait, the final drain and
    // the PR probe share this much of it, leaving the rest for the drain-complete signal.
    static readonly TimeSpan FinalSecondaryProbeDeadline = TimeSpan.FromSeconds(3);

    // A value the server has not settled is re-sent no sooner than this, however often the transcript drains.
    internal static readonly TimeSpan HarnessTitleRetryGap = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Reads the store and sends what the tracker says is owed. Only a post the server took through
    /// <c>/hooks/harness-title</c> latches <see cref="WatchState.HarnessTitleSeen"/>. A post the server answered —
    /// recorded or refused on its merits — settles the value; anything else is re-sent after
    /// <see cref="HarnessTitleRetryGap"/>. The post runs inline in the watch loop, so it is bounded by
    /// <paramref name="budget"/> and <paramref name="beat"/> is called before it.
    /// </summary>
    internal static async Task PostHarnessTitleAsync(
            IHarnessTitleStore                                                        store,
            WatchState                                                                state,
            Func<HarnessTitlePost, TimeSpan, CancellationToken, Task<HarnessTitleOutcome>> post,
            TimeSpan                                                                  budget,
            TimeProvider                                                              time,
            Action                                                                    beat,
            Action<string>                                                            log,
            CancellationToken                                                         ct
        ) {
        var started = time.GetTimestamp();
        var tookUp  = state.HarnessTitleReadInFlight is not null;

        await PollHarnessTitleStoreAsync(store, state, post, budget, time, beat, log, ct);

        // A read taken up from an earlier poll may predate a later change (the final poll before exit is the last
        // chance to see one), so once it has been dealt with the store is read afresh while time remains.
        if (!tookUp || state.HarnessTitleReadInFlight is not null) return;

        await PollHarnessTitleStoreAsync(store, state, post, budget - time.GetElapsedTime(started), time, beat, log, ct);
    }

    static async Task PollHarnessTitleStoreAsync(
            IHarnessTitleStore                                                        store,
            WatchState                                                                state,
            Func<HarnessTitlePost, TimeSpan, CancellationToken, Task<HarnessTitleOutcome>> post,
            TimeSpan                                                                  budget,
            TimeProvider                                                              time,
            Action                                                                    beat,
            Action<string>                                                            log,
            CancellationToken                                                         ct
        ) {
        if (budget <= TimeSpan.Zero) return;

        // The read can be a synchronous SQLite query on a database the agent holds, so it runs off the loop and
        // spends the same budget as the post. One that outlasts the budget is left running and picked up by the next
        // poll, so a stuck store never has more than one read in flight (the readers are not safe to run concurrently).
        var started = time.GetTimestamp();
        var reading = state.HarnessTitleReadInFlight ??= StartRead(store, time);
        StoreTitle? read;
        try {
            read = await reading.Reading.WaitAsync(budget, time, ct);
        } catch (TimeoutException) {
            log("Harness title read did not finish within its budget");
            return;
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            state.HarnessTitleReadInFlight = null;
            log($"Harness title read failed: {ex.Message}");
            return;
        }

        state.HarnessTitleReadInFlight = null;

        // Timed by the read's start, not its finish: a read carried over from an earlier poll must not date a later
        // change after something that happened while it ran.
        var now = time.GetUtcNow();
        if (state.TitleTracker!.Observe(read, reading.StartedAt) is not { } owed) return;

        if (ReferenceEquals(owed, state.LastHarnessTitleAttempted) && now - state.LastHarnessTitlePostAttempt < HarnessTitleRetryGap) return;

        var remaining = budget - time.GetElapsedTime(started);
        if (remaining <= TimeSpan.Zero) return;

        state.LastHarnessTitleAttempted   = owed;
        state.LastHarnessTitlePostAttempt = now;
        beat();

        HarnessTitleOutcome outcome;
        try {
            outcome = await post(owed, remaining, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            state.TitleTracker.OutcomeUnknown();
            throw;
        } catch (Exception ex) {
            state.TitleTracker.OutcomeUnknown();
            log($"Harness title post failed: {ex.Message}");
            return;
        }

        if (outcome is HarnessTitleOutcome.Posted) state.HarnessTitleSeen = true;
        if (outcome is HarnessTitleOutcome.Posted or HarnessTitleOutcome.PostedToLegacyRoute or HarnessTitleOutcome.Refused)
            state.TitleTracker.Settled(owed);
        else if (outcome is HarnessTitleOutcome.Failed) state.TitleTracker.OutcomeUnknown();
        if (outcome is not HarnessTitleOutcome.Posted) log($"Harness title not recorded: {outcome}");
    }

    // Observed from the start: a read no poll ever awaits again must not surface as an unobserved task exception.
    static HarnessTitleRead StartRead(IHarnessTitleStore store, TimeProvider time) {
        var startedAt = time.GetUtcNow();
        var reading   = Task.Run(store.Read, CancellationToken.None);
        reading.ContinueWith(static t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return new HarnessTitleRead(reading, startedAt);
    }

    Task PostHarnessTitleAsync(IHarnessTitleStore store, string sessionId, WatchState state, CancellationToken ct, Action beat,
            TimeSpan? budget = null) =>
        PostHarnessTitleAsync(store, state,
            async (owed, budget, token) => {
                // Client acquisition can refresh a credential over the network, so it spends the same budget.
                var       started  = time.GetTimestamp();
                using var deadline = new CancellationTokenSource(budget, time);
                using var linked   = CancellationTokenSource.CreateLinkedTokenSource(token, deadline.Token);
                using var client   = await http.ForBackgroundAsync(linked.Token);

                var remaining = budget - time.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero) return HarnessTitleOutcome.Failed;

                return await HarnessTitleClient.PostOrFallBackAsync(client, time, Url, sessionId, owed, token, remaining);
            },
            budget ?? SecondaryProbeBudget, time, beat, _log, ct);

    async Task<bool?> ProbeHarnessTitlesAsync(CancellationToken ct, Action beat) {
        beat();
        try {
            var       started  = time.GetTimestamp();
            using var deadline = new CancellationTokenSource(SecondaryProbeBudget, time);
            using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
            using var client   = await http.ForBackgroundAsync(linked.Token);

            var remaining = SecondaryProbeBudget - time.GetElapsedTime(started);
            if (remaining <= TimeSpan.Zero) return null;

            return await HarnessTitleClient.ServerRecordsHarnessTitlesAsync(client, time, Url, remaining, ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            _log($"Harness title probe failed: {ex.Message}");
            return null;
        }
    }

    async Task<bool> PostLinkedPullRequestAsync(string sessionId, RepositoryPayload pr, string source, CancellationToken ct) {
        try {
            using var client  = await http.ForBackgroundAsync(ct);
            var       payload = new JsonObject {
                ["owner"]       = pr.Owner,
                ["repo_name"]   = pr.RepoName,
                ["pr_number"]   = pr.PrNumber,
                ["pr_title"]    = pr.PrTitle,
                ["pr_url"]      = pr.PrUrl,
                ["pr_head_ref"] = pr.PrHeadRef,
                ["branch"]      = pr.Branch,
            };
            using var content = new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
            // The server dedupes by (owner, repo, number), so retrying a throttled or 5xx status is safe.
            using var resp    = await client.PostWithRetryAsync(
                $"{Url}/api/sessions/{Uri.EscapeDataString(sessionId)}/pull-requests", content, time, ct: ct, retryStatuses: true);

            if (resp.IsSuccessStatusCode) _log($"Linked {pr.Owner}/{pr.RepoName}#{pr.PrNumber} {source}");
            else _log($"Linking {pr.Owner}/{pr.RepoName}#{pr.PrNumber} failed: HTTP {(int)resp.StatusCode}");

            return resp.IsSuccessStatusCode;
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            _log($"Linking {pr.Owner}/{pr.RepoName}#{pr.PrNumber} failed: {ex.Message}");
            return false;
        }
    }
}
