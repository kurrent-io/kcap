using System.Diagnostics;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Config;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli;

/// <summary>Launches the watcher as a real detached child of this process.</summary>
public sealed class ProcessWatcherSpawner(
        ConfigRoot config, ProfileContext profiles, WatcherPaths paths, IProcessStarter starter, TimeProvider time)
    : IWatcherSpawner {
    // The one URL this process resolved. The request carries no server: a watcher aimed at a
    // different one than the hook that asked for it would stream a session nothing here can see.
    string? Url => profiles.Resolution.ServerUrl;

    public async Task SpawnAsync(WatcherSpawnRequest request) {
        var (key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, vendor, requestedParentPid) = request;

        // Defence in depth: ShouldSpawnAfter already refuses for an unusable URL, but a caller that
        // bypassed it would otherwise write a PID file asserting capture that cannot happen — a
        // watcher streams to SignalR and can never connect here.
        if (!HookHttp.IsPostable(Url)) {
            await (request.Stderr ?? Console.Error).WriteLineAsync(
                UnusableUrlDiagnostic.Build(profiles.Resolution.Source, Url, $"watcher not started for {key}"));
            return;
        }

        try {
            var watcherDir = paths.Directory;
            Directory.CreateDirectory(watcherDir);

            var kcapPath = Environment.ProcessPath ?? "kcap";
            // The long-lived agent, not getppid(): hooks run under a transient executor that dies when the hook
            // returns, and following it either ends the session or arms nothing.
            // Copilot's follows only a process named as Copilot's. A VS Code Local session has none, and a process
            // above it that happens to match says nothing about the session.
            var parentPid     = requestedParentPid
                             ?? (vendor != "copilot"                                ? ProcessHelpers.GetCodingAgentPid(vendor)
                                 : CopilotPaths.IsVsCodeLocalTranscript(transcriptPath) ? null
                                 :                                                        ProcessHelpers.GetCopilotHostPid());
            var arguments     = BuildSpawnArgs(key, transcriptPath, agentId, sessionIdOverride, cwd, skipTitle, parentPid, vendor);

            var psi = new ProcessStartInfo(kcapPath, arguments) {
                RedirectStandardOutput = true,
                RedirectStandardInput  = true,
                RedirectStandardError  = true,
                UseShellExecute        = false,
                CreateNoWindow         = true,
                Environment = {
                    [ProfileOverrides.UrlVar]    = Url,
                    [ConfigRoot.ConfigDirEnvVar] = config.Directory
                }
            };

            // An empty value reads as unset, so a watcher following no known run carries none of this one's.
            if (request.RunsAs is { } run) {
                psi.Environment[HostedAgent.AgentIdVar]     = run.AgentId ?? "";
                psi.Environment[HostedAgent.DaemonEpochVar] = run.DaemonEpoch ?? "";
            }

            // The watcher outlives the hook that spawns it, so it must carry none of the
            // agent's handles with it — an inherited hook-stdout pipe would stay open for the
            // watcher's whole lifetime and the agent's read would never reach EOF.
            if (starter.StartDetached(psi) is not { } pid) {
                await (request.Stderr ?? Console.Error).WriteLineAsync($"Failed to spawn watcher for {key}");

                return;
            }

            // Line 2 is this incarnation's start-identity token (daemon pid-file layout) so
            // KillWatcher can tell the spawned watcher apart from a later recycle of its pid.
            var token = ProcessStartToken.ForPid(pid);
            await File.WriteAllTextAsync(
                paths.PidFile(key), token is null ? pid.ToString() : $"{pid}\n{token}");

            // This instance's start time, so a later staleness probe knows whether it is still
            // within the startup grace window. Written here rather than by the watcher itself, so
            // it exists even if the child never gets far enough to touch its own heartbeat.
            try {
                WatcherHeartbeat.Touch(paths.StartedFile(key), time.GetUtcNow());
            } catch {
                /* best-effort — a missing marker just means IsWatcherAlive treats "now" as startupAt */
            }
        } catch (Exception ex) {
            await (request.Stderr ?? Console.Error).WriteLineAsync($"Failed to spawn watcher for {key}: {ex.Message}");
        }
    }

    internal static string BuildSpawnArgs(
            string  key,
            string  transcriptPath,
            string? agentId,
            string? sessionIdOverride,
            string? cwd,
            bool    skipTitle,
            int?    parentPid,
            string  vendor
        ) {
        var sessionId = sessionIdOverride ?? key;

        var arguments = agentId is not null
            ? $"watch {sessionId} \"{transcriptPath}\" --agent-id {agentId}"
            : $"watch {key} \"{transcriptPath}\"";

        if (cwd is not null) {
            arguments += $" --cwd \"{cwd}\"";
        }

        if (skipTitle) {
            arguments += " --skip-title";
        }

        if (parentPid is { } ppid and > 1) {
            arguments += $" --parent-pid {ppid}";
        }

        arguments += $" --vendor \"{vendor}\"";

        return arguments;
    }
}
