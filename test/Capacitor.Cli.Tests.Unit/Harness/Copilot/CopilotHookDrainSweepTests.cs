using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Recording.Harness.Copilot;

namespace Capacitor.Cli.Tests.Unit.Harness.Copilot;

/// <summary>A Copilot hook shares Copilot's shutdown budget with every session it ends, and a pending end's transcript read
/// has no bound, so its drain leaves pending ends to the finalizer and the daemon. Other hooks sweep them.</summary>
public class CopilotHookDrainSweepTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    const string Server  = "https://kcap.example";
    const string Session = "0f1e2d3c4b5a69788796a5b4c3d2e1f0";
    const string End     = """{"session_id":"0f1e2d3c4b5a69788796a5b4c3d2e1f0","reason":"parent_exited"}""";
    const string Start   = """{"type":"session.start","data":{},"id":"run-a","timestamp":1}""";
    const string Down    = """{"type":"session.shutdown","data":{},"id":"evt-2","timestamp":2}""";

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task Only_a_drain_outside_a_Copilot_hook_sweeps_a_due_pending_end(bool sweep) {
        var paths      = WatcherPaths.FromEnvironment(Config.Root);
        var transcript = Config.CreateFile("events.jsonl", Start + "\n" + Down + "\n");
        CopilotPendingEnd.Write(paths, Session, End, Server, runtimePid: null, transcript, TimeProvider.System, runId: "run-a");

        var lifecycle = new HookSpool(Config.Root, TimeProvider.System, Server);
        var http      = new FixedCapacitorHttpClient();
        var profiles  = Resolutions.At(Server, Config.Root);
        var poster    = new AgentHookPoster(Config.Root, profiles, http, TestWatchers.For(Config.Root, profiles, http), TimeProvider.System);
        using var client = new HttpClient();

        await poster.DrainSpoolsCoreAsync(lifecycle, new TranscriptSpool(Config.Root, TimeProvider.System, Server), null,
            _ => Task.FromResult(new AuthAttempt(client, AuthStatus.Expired)), sweep);

        await Assert.That(CopilotPendingEnd.SessionsWithPending(paths).Contains(Session)).IsEqualTo(!sweep);
        await Assert.That(lifecycle.HasBacklog(Session)).IsEqualTo(sweep);
    }
}
