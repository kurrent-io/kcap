namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Lists Codex's skills for a directory through a short-lived <c>codex app-server</c>, without starting a
/// thread, so no model call is made. Every failure returns an empty list.</summary>
public static class CodexSkillProbe {
    public static async Task<IReadOnlyList<HostedAgentCommand>> ProbeAsync(
            HarnessRegistry   harnesses,
            string            workingDir,
            UserHome          home,
            TimeProvider      time,
            TimeSpan          timeout,
            Action<string>    log,
            CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Codex) is not { } exe) return [];

        using var process = CliProcess.TryStart(CodexAppServer.StartInfo(exe, null, home), log);
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
            await CodexAppServer.ShutDownAsync(process, time).ConfigureAwait(false);
        }
    }
}
