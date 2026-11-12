using System.Diagnostics;
using System.Text.Json.Nodes;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>
/// Real-binary proof that the Copilot finalizer outlives the termination signals sent to everything VS Code started
/// when it quits, which arrive before Copilot has written <c>session.shutdown</c>.
/// </summary>
[RunOn(OS.Linux | OS.MacOs)]
public class CopilotFinalizeSignalTests : IDisposable {
    const string ShutdownLine = """{"type":"session.shutdown","data":{},"id":"evt-shutdown","timestamp":1234567890}""";

    [TempDir]         public required TempDir         Tmp     { get; init; }
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }

    readonly WireMockServer _server = WireMockServer.Start();

    public void Dispose() => _server.Stop();

    [Test]
    [Arguments(15)]
    [Arguments(1)]
    public async Task A_signalled_finalizer_still_ends_the_session_once_shutdown_lands(int signal) {
        var sid        = Guid.NewGuid().ToString("N");
        var transcript = Tmp.CreateFile("events.jsonl", """{"type":"assistant.message","data":{"content":"hi"},"id":"evt-1","timestamp":1}""" + "\n");
        var endBody    = new JsonObject { ["session_id"] = sid, ["reason"] = "user_exit", ["ended_at"] = "2026-11-10T16:55:41.7260000+00:00" }.ToJsonString();

        _server.Given(Request.Create().WithPath($"/api/sessions/{sid}/last-line").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(200).WithHeader("Content-Type", "application/json").WithBody("""{"last_line_number": -1}"""));
        _server.Given(Request.Create().WithPath("/hooks/transcript").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200).WithBody("""{"processed":0}"""));
        _server.Given(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(200));

        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "copilot-finalize", sid, transcript, endBody);
        psi.WorkingDirectory        = Tmp.Path;
        psi.Environment["KCAP_URL"] = _server.Url!;

        using var finalizer = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap");
        finalizer.StandardInput.Close();
        var identity = PidIdentity.Capture(finalizer.Id);

        try {
            // Its log is opened just before it starts waiting for the shutdown line.
            var log = Config.PathTo("logs", $"{sid}-finalize.log");
            for (var i = 0; i < 100 && !File.Exists(log); i++) await Task.Delay(100);
            await Task.Delay(500);

            using (var kill = Process.Start(new ProcessStartInfo("/bin/kill", [$"-{signal}", finalizer.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)]))!) {
                await kill.WaitForExitAsync();
                await Assert.That(kill.ExitCode).IsEqualTo(0);
            }

            await Task.Delay(500);
            await File.AppendAllTextAsync(transcript, ShutdownLine + "\n");

            var ended = await WaitForSessionEndAsync(TimeSpan.FromSeconds(20));

            await Assert.That(ended).IsNotNull().Because(File.Exists(log) ? File.ReadAllTextShared(log) : "no finalize log");
            await Assert.That(JsonNode.Parse(ended!)!["ended_at"]?.GetValue<string>()).IsEqualTo("2026-11-10T16:55:41.7260000+00:00");
        } finally {
            if (!PidIdentity.IsGone(finalizer.Id, identity)) finalizer.Kill();
        }
    }

    async Task<string?> WaitForSessionEndAsync(TimeSpan budget) {
        var deadline = DateTime.UtcNow + budget;

        while (DateTime.UtcNow < deadline) {
            var entries = _server.FindLogEntries(Request.Create().WithPath("/hooks/session-end/copilot").UsingPost());
            if (entries.Count > 0) return entries[0].RequestMessage.Body;
            await Task.Delay(100);
        }

        return null;
    }
}
