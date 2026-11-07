using System.Text.Json;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Resume;

namespace Capacitor.Cli.Core.Harness.Muse;

/// <summary>
/// Sends the handed Muse ends no watcher took: a run that ended with nothing left to arm a watcher, or whose watcher
/// could send its end neither way, would otherwise keep its session open for good. Each is moved into the lifecycle
/// spool, whose drain sends it behind whatever transcript the session still has spooled.
/// </summary>
public static class MuseHandedEndSweep {
    public const string Route = "session-end/muse";

    /// <summary>A live watcher takes a handed end within a tick and drains the log before sending it; one still waiting
    /// this long has no watcher, and sending it sooner could overtake lines a watcher is still draining.</summary>
    public static readonly TimeSpan Unattended = TimeSpan.FromMinutes(5);

    /// <summary>Spools into <paramref name="lifecycle"/>, the spool for <paramref name="server"/>, every unattended
    /// request that is due and was handed over for that server, and returns how many. A request for another server is
    /// left to that server's drain; one written before requests recorded a server is taken by whichever drain sweeps
    /// it. A request a resume holds, as the watcher would have held it, is dropped, and so is one for a session a person
    /// disabled. Never throws.</summary>
    public static int SpoolDue(WatcherPaths paths, HookSpool lifecycle, string? server, DaemonStore daemons, TimeProvider time) {
        var spooled = 0;

        try {
            foreach (var session in MuseSessionEndRequest.SessionsWithPending(paths)) {
                var early  = new List<MuseSessionEndRequest>();
                var failed = new List<MuseSessionEndRequest>();

                while (MuseSessionEndRequest.TryClaim(paths, session, "sweep", time, recorded => recorded is null || ServerIdentity.SameServer(recorded, server)) is { } handed) {
                    if (lifecycle.IsDisabledByPerson(session)) {
                        MuseSessionEndRequest.Release(handed.ClaimedPath);
                    } else if (time.GetUtcNow() - handed.HandedAt < Unattended) {
                        early.Add(handed);
                    } else if (MuseSessionEndRequest.ResumedSince(paths, session, handed.HandedAt) || HeldForResume(handed.Payload, session, daemons, time)) {
                        MuseSessionEndRequest.Release(handed.ClaimedPath);
                    } else if (lifecycle.Append(session, Route, Spoolable(handed.Payload, session))) {
                        MuseSessionEndRequest.Release(handed.ClaimedPath);
                        spooled++;
                    } else {
                        failed.Add(handed);
                    }
                }

                foreach (var handed in early) MuseSessionEndRequest.Unclaim(handed);
                foreach (var handed in failed) MuseSessionEndRequest.Defer(handed, time);
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The next pass tries again.
        }

        return spooled;
    }

    /// <summary>Whether a resume holds the end, judged by the fence every hook and watcher uses: as the run it names, or
    /// placed by its session when it names none.</summary>
    static bool HeldForResume(string payload, string session, DaemonStore daemons, TimeProvider time) {
        string? agentId = null, epoch = null;

        try {
            if (JsonNode.Parse(payload) is JsonObject end && end["agent_host_id"]?.GetValue<string>() is { Length: > 0 } named) {
                agentId = named;
                epoch   = end["daemon_epoch"]?.GetValue<string>() is { Length: > 0 } e ? e : null;
            }
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) { }

        return ResumeEndFence.ShouldHold(agentId, epoch, session, daemons, time);
    }

    /// <summary>The handed end with the fields every session-end binding requires, which the watcher would have added.</summary>
    static string Spoolable(string payload, string session) {
        JsonObject end;
        try {
            end = JsonNode.Parse(payload) as JsonObject ?? new JsonObject();
        } catch (JsonException) {
            end = new JsonObject { ["reason"] = "other" };
        }

        end["session_id"]        = session;
        end["transcript_path"] ??= "";
        end["cwd"]             ??= "";
        end["hook_event_name"] ??= "SessionEnd";

        return end.ToJsonString();
    }
}
