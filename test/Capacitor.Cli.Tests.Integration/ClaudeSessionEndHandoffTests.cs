using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Real-binary proof that the Claude SessionEnd hook does not wait for the session-end it hands
/// off, while the session-end still reaches the server. The server holds <c>/hooks/session-end</c>
/// until the hook has exited, so the only way the POST can be answered is in a process that
/// outlived the hook.
/// </summary>
public class ClaudeSessionEndHandoffTests : IDisposable {
    /// <summary>The longest the server holds the POST for a hook that has not exited: long enough that a
    /// hook which waited for it could not be mistaken for a slow one, as the bound below sits halfway.</summary>
    static readonly TimeSpan ServerStall = TimeSpan.FromSeconds(6);

    /// <summary>How long after the POST reached the server a detached hook may still be running. Counted
    /// from the POST's arrival rather than from the spawn, so the runner's process start is not in it:
    /// a hook that waited is still there when the stall ends, and one that detached has usually exited
    /// before its continuation has started.</summary>
    static readonly TimeSpan ReturnsWithout = TimeSpan.FromSeconds(3);

    [TempDir]         public required TempDir         Tmp     { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    public async Task Hook_returns_at_once_and_the_detached_continuation_posts_session_end() {
        var sid        = Guid.NewGuid().ToString("N");
        var transcript = Tmp.CreateFile("t.jsonl", """{"type":"user","uuid":"u1","message":{"role":"user","content":"hi"}}""" + "\n");

        var clock = Stopwatch.StartNew();
        var postArrivedTicks = 0L;

        using var hookExited = new ManualResetEventSlim();
        _server.Given(Request.Create().WithPath("/hooks/session-end").UsingPost())
            .RespondWith(Response.Create().WithCallback(_ => {
                Interlocked.CompareExchange(ref postArrivedTicks, clock.Elapsed.Ticks, 0);
                hookExited.Wait(ServerStall);

                return new WireMock.ResponseMessage {
                    StatusCode = 200,
                    BodyData   = new WireMock.Util.BodyData { DetectedBodyType = WireMock.Types.BodyType.String, BodyAsString = "{}" },
                };
            }));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"processed":0}"""));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "hook", "--claude", "--no-update-check");
        psi.WorkingDirectory        = Tmp.Path;
        psi.Environment["KCAP_URL"] = _server.Url!;

        var payload = new JsonObject {
            ["hook_event_name"] = "SessionEnd",
            ["session_id"]      = sid,
            ["transcript_path"] = transcript,
            ["cwd"]             = Tmp.Path,
            ["reason"]          = "exit",
        }.ToJsonString();

        using var hook = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap");
        await hook.StandardInput.WriteAsync(payload);
        hook.StandardInput.Close();
        var stdout = hook.StandardOutput.ReadToEndAsync();
        var stderr = hook.StandardError.ReadToEndAsync();
        await hook.WaitForExitAsync();
        var exited = clock.Elapsed;
        hookExited.Set();

        // Read after the exit: a POST that had not arrived by then was not waited for at all.
        var postArrived     = Interlocked.Read(ref postArrivedTicks) is var ticks and > 0 ? TimeSpan.FromTicks(ticks) : exited;
        var outlivedThePost = exited - postArrived;

        await Assert.That(hook.ExitCode).IsEqualTo(0).Because(await stderr);
        await Assert.That(await stdout).IsEmpty();
        // The hook was gone before the stalled POST could have completed, so it cannot have waited
        // for it — which is the whole property, on a session with a transcript to drain.
        await Assert.That(outlivedThePost).IsLessThan(ReturnsWithout).Because($"stderr: {await stderr}");

        // The hook is gone; the continuation's POST is answered from here on.
        var posted = await WaitForSessionEndAsync(TimeSpan.FromSeconds(20));

        await Assert.That(posted).IsNotNull().Because("no /hooks/session-end reached the server");
        await Assert.That(posted!["session_id"]?.GetValue<string>()).IsEqualTo(sid);
        await Assert.That(posted["reason"]?.GetValue<string>()).IsEqualTo("exit");
        await Assert.That(posted["ended_at"]?.GetValue<string>()).IsNotNull();

        // The continuation's output lands in the session log, where the watcher's already goes.
        var log = Config.PathTo("logs", $"{sid}.log");
        await Assert.That(File.Exists(log)).IsTrue();
        await Assert.That(File.ReadAllTextShared(log)).Contains($"Inline drain for {sid}");
    }

    async Task<JsonNode?> WaitForSessionEndAsync(TimeSpan budget) {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline) {
            var entries = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end").UsingPost());
            if (entries.Count > 0) return JsonNode.Parse(entries[0].RequestMessage.Body!);
            await Task.Delay(100);
        }

        return null;
    }
}
