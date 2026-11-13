using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Pi;

/// <summary>Never sends a prompt, so no model call is made.</summary>
public static class PiCommandProbe {
    public static async Task<IReadOnlyList<HostedAgentCommand>> ProbeAsync(
            HarnessRegistry   harnesses,
            string            workingDir,
            TimeProvider      time,
            TimeSpan          timeout,
            Action<string>    log,
            CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Pi) is not { } exe) return [];

        using var process = CliProcess.TryStart(PiRpcProbe.StartInfo(exe, workingDir), log);
        if (process is null) return [];
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);

        try {
            await process.StandardInput.WriteAsync(($$"""{"type":"{{PiCommands.Command}}"}""" + "\n").AsMemory(), linked.Token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(linked.Token).ConfigureAwait(false);

            // Stdin stays open until the answer: Pi takes EOF as shutdown without finishing a command in flight.
            while (await process.StandardOutput.ReadLineAsync(linked.Token).ConfigureAwait(false) is { } line) {
                if (Answer(line) is { } commands) return commands;
            }

            return [];
        } catch (Exception ex) {
            log($"pi command probe failed: {ex.Message}");
            return [];
        } finally {
            await VendorProcess.ShutDownAsync(process, time).ConfigureAwait(false);
        }
    }

    static IReadOnlyList<HostedAgentCommand>? Answer(string line) {
        try {
            using var doc = JsonDocument.Parse(line);

            return PiRpcProbe.Answers(doc.RootElement, PiCommands.Command) ? PiCommands.Extract(doc.RootElement) : null;
        } catch (JsonException) {
            return null;
        }
    }
}
