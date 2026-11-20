using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Capacitor.Cli.Tests.Integration;

/// <summary>The real binary's stdio session host: it answers the handshake, reports a daemon nobody runs, writes
/// nothing but protocol lines to stdout, exits 0 when its input closes, and stays out of the usage text.</summary>
[ParallelLimiter<SubprocessLimit>]
public class IdeCommandTests {
    // Nothing listens on the discard port, so the server connection fails fast instead of reaching a real server.
    const string UnreachableServer = "http://127.0.0.1:9";

    static readonly TimeSpan ReplyWait = TimeSpan.FromSeconds(15);
    static readonly TimeSpan ExitWait  = TimeSpan.FromSeconds(15);

    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [GitRepo]         public required GitRepo         Repo    { get; init; }

    [Test]
    public async Task Initialize_is_answered_and_closing_stdin_exits_zero() {
        using var proc = Start(UnreachableServer);
        try {
            await SendAsync(proc, Request(1, "initialize", InitializeParams()));
            var reply = await ReadUntilAsync(proc, message => message["id"]?.GetValue<int>() == 1);

            await Assert.That(reply["error"]).IsNull();
            await Assert.That(reply["result"]?["protocol_version"]?.GetValue<int>()).IsEqualTo(1);
            await Assert.That(reply["result"]?["cli_version"]?.GetValue<string>()).IsNotNullOrEmpty();
            await Assert.That(reply["result"]?["capabilities"]?.AsArray().Select(c => c?.GetValue<string>())).Contains("sessions/1");

            await Assert.That(await CloseAndWaitAsync(proc)).IsEqualTo(0);
        } finally {
            Kill(proc);
        }
    }

    [Test]
    public async Task With_no_daemon_running_the_daemon_is_reported_unreachable() {
        using var proc = Start(UnreachableServer);
        try {
            await SendAsync(proc, Request(1, "initialize", InitializeParams()));
            var changed = await ReadUntilAsync(proc, message => message["method"]?.GetValue<string>() == "connection/changed",
                TimeSpan.FromSeconds(5));

            await Assert.That(changed["params"]?["daemon"]?["state"]?.GetValue<string>()).IsIn("connecting", "unreachable");
        } finally {
            Kill(proc);
        }
    }

    /// <summary>The attach loop says <c>connecting</c> only when it starts, so one after <c>unreachable</c> is the retry's restart.</summary>
    [Test]
    public async Task Retry_restarts_a_daemon_attach_that_is_waiting_to_retry() {
        using var proc = Start(UnreachableServer);
        try {
            await SendAsync(proc, Request(1, "initialize", InitializeParams()));
            await ReadUntilAsync(proc, message => DaemonState(message) == "unreachable");

            await SendAsync(proc, Request(2, "connection/retry", new JsonObject()));
            var reply = await ReadUntilAsync(proc, message => message["id"]?.GetValue<int>() == 2);
            await ReadUntilAsync(proc, message => DaemonState(message) == "connecting", TimeSpan.FromSeconds(5));

            await Assert.That(reply["result"]?.ToJsonString()).IsEqualTo("{}");
            await Assert.That(await CloseAndWaitAsync(proc)).IsEqualTo(0);
        } finally {
            Kill(proc);
        }
    }

    [Test]
    public async Task Nothing_but_protocol_lines_reaches_stdout() {
        using var proc = Start(UnreachableServer);
        var stderr = proc.StandardError.ReadToEndAsync();
        try {
            await SendAsync(proc, Request(1, "initialize", InitializeParams()));
            await SendAsync(proc, Request(2, "sessions/subscribe", new JsonObject {
                ["subscription_id"] = "s1",
                ["git_roots"]       = new JsonArray(Repo.Path),
            }));
            await SendAsync(proc, Request(3, "no/such_method", new JsonObject()));
            // Long enough for the daemon client and the server connection to fail and report it.
            await Task.Delay(TimeSpan.FromSeconds(2));
            proc.StandardInput.Close();

            using var cts = new CancellationTokenSource(ExitWait);
            var stdout = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            var lines = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            var methods = new List<string?>();
            foreach (var line in lines) {
                JsonObject? message;
                try {
                    message = JsonNode.Parse(line) as JsonObject;
                } catch (JsonException) {
                    message = null;
                }

                await Assert.That(message).IsNotNull().Because($"stdout line is not a JSON object: {line}");
                await Assert.That(message!["jsonrpc"]?.GetValue<string>()).IsEqualTo("2.0").Because($"stdout line: {line}");
                methods.Add(message["method"]?.GetValue<string>());
            }

            // Guards the loop above: the snapshot proves the subscription ran, so the lines were not vacuous.
            await Assert.That(methods).Contains("sessions/snapshot").Because($"stdout: {stdout}\nstderr: {await stderr}");
            await Assert.That(proc.ExitCode).IsEqualTo(0);
        } finally {
            Kill(proc);
        }
    }

    [Test]
    public async Task With_no_server_configured_it_still_answers_initialize() {
        using var proc = Start(serverUrl: null);
        try {
            await SendAsync(proc, Request(1, "initialize", InitializeParams()));
            var reply = await ReadUntilAsync(proc, message => message["id"]?.GetValue<int>() == 1);
            await Assert.That(reply["result"]?["protocol_version"]?.GetValue<int>()).IsEqualTo(1);

            var changed = await ReadUntilAsync(proc, message => message["method"]?.GetValue<string>() == "connection/changed");
            await Assert.That(changed["params"]?["server"]?["state"]?.GetValue<string>()).IsEqualTo("dormant");

            await Assert.That(await CloseAndWaitAsync(proc)).IsEqualTo(0);
        } finally {
            Kill(proc);
        }
    }

    [Test]
    public async Task The_command_is_not_in_usage() {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "--help");
        psi.WorkingDirectory = Repo.Path;
        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
        try {
            using var cts = new CancellationTokenSource(ExitWait);
            var usage = await proc.StandardOutput.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);

            await Assert.That(usage).Contains("Usage: kcap");
            var verbs = usage.Split('\n')
                .Where(line => line.StartsWith("  ", StringComparison.Ordinal))
                .Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault());
            await Assert.That(verbs).DoesNotContain("ide");
        } finally {
            Kill(proc);
        }
    }

    Process Start(string? serverUrl) {
        var psi = KcapProcess.StartInfo(Daemons.Store, Config.Root, "ide");
        psi.WorkingDirectory = Repo.Path;
        if (serverUrl is not null) psi.Environment["KCAP_URL"] = serverUrl;

        return Process.Start(psi) ?? throw new InvalidOperationException("Failed to start kcap process");
    }

    static JsonObject InitializeParams() => new() {
        ["protocol_version"] = 1,
        ["client"]           = new JsonObject { ["name"] = "kcap-tests", ["version"] = "0" },
    };

    static string? DaemonState(JsonObject message) =>
        message["method"]?.GetValue<string>() == "connection/changed" ? message["params"]?["daemon"]?["state"]?.GetValue<string>() : null;

    static JsonObject Request(int id, string method, JsonObject parameters) =>
        new() { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = parameters };

    static async Task SendAsync(Process proc, JsonObject request) {
        await proc.StandardInput.WriteLineAsync(request.ToJsonString());
        await proc.StandardInput.FlushAsync();
    }

    static async Task<JsonObject> ReadUntilAsync(Process proc, Func<JsonObject, bool> match, TimeSpan? within = null) {
        using var cts = new CancellationTokenSource(within ?? ReplyWait);
        while (true) {
            var line = await proc.StandardOutput.ReadLineAsync(cts.Token)
                    ?? throw new InvalidOperationException($"kcap ide closed stdout. Stderr: {await proc.StandardError.ReadToEndAsync()}");
            if (JsonNode.Parse(line) is JsonObject message && match(message)) return message;
        }
    }

    static async Task<int> CloseAndWaitAsync(Process proc) {
        proc.StandardInput.Close();
        using var cts = new CancellationTokenSource(ExitWait);
        // Drained so a notification written on the way out cannot fill the pipe and hold the exit.
        _ = proc.StandardOutput.ReadToEndAsync(cts.Token);
        await proc.WaitForExitAsync(cts.Token);

        return proc.ExitCode;
    }

    static void Kill(Process proc) {
        try {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
        } catch {
            // already gone
        }
    }
}
