using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Never starts a thread, so no model call is made.</summary>
public static class CodexSkillProbe {
    public static async Task<IReadOnlyList<HostedAgentCommand>> ProbeAsync(
            HarnessRegistry   harnesses,
            string            workingDir,
            string?           accountDirectory,
            UserHome          home,
            TimeProvider      time,
            TimeSpan          timeout,
            Action<string>    log,
            CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Codex) is not { } exe) return [];

        using var process = CliProcess.TryStart(CodexAppServer.StartInfo(exe, accountDirectory, home), log);
        if (process is null) return [];
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(timeout, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var rpc = new CodexAppServerRpc(process.StandardInput, process.StandardOutput, linked.Token);

        try {
            await rpc.InitializeAsync().ConfigureAwait(false);
            var result = await rpc.RequestAsync("skills/list", w => {
                w.WriteStartArray("cwds");
                w.WriteStringValue(workingDir);
                w.WriteEndArray();
            }).ConfigureAwait(false);

            return CodexSkills.Extract(result);
        } catch (Exception ex) {
            log($"codex skill probe failed: {ex.Message}");
            return [];
        } finally {
            await VendorProcess.ShutDownAsync(process, time).ConfigureAwait(false);
        }
    }
}
