using System.Globalization;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Harness.Dsh;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.PrDetection;

namespace Capacitor.Cli.Commands.Harness;

/// <summary>
/// Lifecycle events from kcap's dsh plugin, passed as arguments rather than a stdin payload.
/// session-start posts the session and starts the watcher on the JSONL the plugin appends to;
/// session-end drains that watcher before posting, so the server's stats cover the whole transcript.
/// The plugin passes dsh's own pid: dsh runs as <c>node</c>, which an ancestry walk cannot tell apart
/// from any other node process.
/// </summary>
sealed class DshHookCommand(
        ConfigRoot config, ProfileContext profiles, HookClock clock, UserHome home,
        HarnessRegistry harnesses, HostedAgent hosted, ICapacitorHttpClient http, WatcherManager watchers,
        GitProviderRouter router) {
    readonly AgentHookPoster _poster = new(config, profiles, http, watchers, clock.Time);

    string Url => profiles.Resolution.ServerUrl!;

    /// <summary>Bounds the drain so a stuck watcher cannot hold back the session-end POST.</summary>
    static readonly TimeSpan PreHookDrainCap = TimeSpan.FromSeconds(6);

    public async Task<int> Handle(string[] args) {
        var eventName = GetArg(args, "--event");
        if (string.IsNullOrWhiteSpace(eventName)) {
            await Console.Error.WriteLineAsync(
                "kcap hook --dsh requires --event <session-start|session-end> "
              + "(the kcap dsh plugin passes it; re-run: kcap plugin install --dsh)");
            return 1;
        }

        if (GetArg(args, "--session") is not { Length: > 0 } rawId) return 0;
        var sessionId = DshSessionId.Canonicalize(rawId);

        var file = GetArg(args, "--file");
        if (string.IsNullOrWhiteSpace(file)) return 0;

        var cwd = GetArg(args, "--cwd");

        if (DisabledSessions.IsDisabled(sessionId, config)) {
            if (eventName == "session-end") DisabledSessions.RemoveMarker(sessionId, config);
            return 0;
        }

        var activeProfile = profiles.Effective;
        if (PathExclusion.IsOutOfScope(cwd, activeProfile?.AllowedPaths, activeProfile?.ExcludedPaths, home)) return 0;

        return eventName switch {
            "session-start" => await HandleSessionStart(sessionId, file, cwd, args, activeProfile),
            "session-end"   => await HandleSessionEnd(sessionId, file, cwd, GetArg(args, "--reason")),
            _               => 0,
        };
    }

    async Task<int> HandleSessionStart(string sessionId, string file, string? cwd, string[] args, Profile? activeProfile) {
        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionStart",
            ["session_id"]      = sessionId,
            ["home_dir"]        = home.Path,
            ["started_at"]      = clock.Time.GetUtcNow().ToString("O"),
        };

        if (cwd is not null) {
            forwarded["cwd"] = cwd;
            if (GitRepository.FindRoot(cwd) is { } workspaceRoot) forwarded["workspace_root"] = workspaceRoot;
        }

        if (GetArg(args, "--parent-session") is { Length: > 0 } parent)
            forwarded["parent_session_id"] = DshSessionId.Canonicalize(parent);

        if (hosted.AgentId is { } agentHostId) forwarded["agent_host_id"] = agentHostId;

        // Before enrichment, so it survives the round-trip; null lets the server fall back to the
        // org-repo visibility.
        if (activeProfile?.DefaultVisibility is { } visibility) forwarded["default_visibility"] = visibility;

        SessionStartInventory.Stamp(forwarded, config, harnesses, HarnessId.Dsh, clock.Time, file);
        var enriched = await RepositoryDetection.EnrichWithRepositoryInfo(router, config, forwarded.ToJsonString(), clock.Time);

        if (await RepoExclusion.IsOutOfScopeAsync(router, config, enriched,
                                                  activeProfile?.AllowedRepos, activeProfile?.ExcludedRepos, clock.Time)) {
            DisabledSessions.Mark(sessionId, config);
            return 0;
        }

        var spool   = new HookSpool(config, clock.Time, Url);
        var outcome = await _poster.PostOrSpoolAsync("session-start/dsh", enriched, "dsh-hook",
            spool, sessionId, route: "session-start/dsh");

        if (!AgentHookPoster.ShouldSpawnAfter(outcome, Url)) return outcome == HookPostOutcome.Failed ? 1 : 0;

        await watchers.EnsureWatcherRunning(sessionId, file,
            agentId: null, sessionIdOverride: null, cwd: cwd,
            skipTitle: false, vendor: HarnessId.Dsh.VendorId,
            parentPid: AgentPidOf(args));

        return 0;
    }

    async Task<int> HandleSessionEnd(string sessionId, string file, string? cwd, string? reason) {
        try {
            var drained = await TimeBudget.RunCappedAsync(
                async () => {
                    await watchers.KillWatcher(sessionId);
                    await watchers.InlineDrainAsync(sessionId, file, agentId: null, vendor: HarnessId.Dsh.VendorId);
                },
                PreHookDrainCap, clock.Time);

            if (!drained) {
                await Console.Error.WriteLineAsync(
                    $"[kcap] dsh session-end pre-drain cap ({PreHookDrainCap.TotalSeconds:0}s) elapsed; proceeding to POST. "
                  + $"Transcript tail may be incomplete — recoverable via: kcap import --dsh --session {sessionId}");
            }
        } catch (Exception ex) {
            await Console.Error.WriteLineAsync($"[kcap] dsh session-end pre-hook failed: {ex.Message}");
        }

        var forwarded = new JsonObject {
            ["hook_event_name"] = "sessionEnd",
            ["session_id"]      = sessionId,
            ["reason"]          = string.IsNullOrEmpty(reason) ? "disposed" : reason,
            ["home_dir"]        = home.Path,
            ["ended_at"]        = clock.Time.GetUtcNow().ToString("O"),
        };

        if (cwd is not null) forwarded["cwd"] = cwd;
        if (hosted.AgentId is { } agentHostId) forwarded["agent_host_id"] = agentHostId;

        var outcome = await _poster.PostAsync("session-end/dsh", forwarded.ToJsonString(), "dsh-hook");
        return outcome == HookPostOutcome.Failed ? 1 : 0;
    }

    /// <summary>dsh's pid as the plugin reports it, or null when absent or not a live-looking pid
    /// (the watcher then resolves the agent from its own ancestry).</summary>
    internal static int? AgentPidOf(string[] args) =>
        int.TryParse(GetArg(args, "--agent-pid"), NumberStyles.None, CultureInfo.InvariantCulture, out var pid) && pid > 1
            ? pid
            : null;

    static string? GetArg(string[] args, string flag) {
        var idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
