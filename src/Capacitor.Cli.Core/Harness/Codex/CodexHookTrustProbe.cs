using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Asks <c>codex app-server</c> <c>hooks/list</c> whether Codex trusts kcap's hooks in one Codex home.
/// Only Codex can answer: it keeps trust as a hash of each hook's definition, computed its own way, so kcap never
/// reads or writes that state itself.</summary>
public sealed class CodexHookTrustProbe(HarnessRegistry harnesses, UserHome home, TimeProvider time) {
    internal TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(5);

    public async Task<CodexHooksTrust> ProbeAsync(CodexPaths paths, CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Codex) is not { } exe) return CodexHooksTrust.Unknown;

        var psi    = CodexAppServer.StartInfo(exe, paths.Home, home);
        var before = CodexTrustFingerprint.Of(paths);

        using var process = CliProcess.TryStart(psi, _ => { });
        if (process is null) return CodexHooksTrust.Unknown;

        // Drained so a chatty stderr cannot fill the pipe and stall the answer.
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var rpc = new CodexAppServerRpc(process.StandardInput, process.StandardOutput, linked.Token);

        HookTrustObservation? observation = null;
        try {
            await rpc.InitializeAsync().ConfigureAwait(false);
            observation = await ReadAsync(rpc, psi.WorkingDirectory, paths, before).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            // No answer within the deadline.
        } catch (Exception ex) when (ex is CodexAppServerRpc.CallFailed or IOException or ObjectDisposedException) {
            // Codex refused the handshake or stopped.
        } finally {
            await CodexAppServer.ShutDownAsync(process, time).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        return observation?.Trust switch {
            HookTrustStates.Trusted   => CodexHooksTrust.Trusted,
            HookTrustStates.Untrusted => CodexHooksTrust.Untrusted,
            _                         => CodexHooksTrust.Unknown,
        };
    }

    /// <summary>Null when Codex gives no usable answer or either Codex file changed or was unreadable around it:
    /// a verdict that cannot be tied to the bytes it was given for is no verdict.</summary>
    internal static async Task<HookTrustObservation?> ReadAsync(CodexAppServerRpc rpc, string cwd, CodexPaths paths, string? before) {
        if (before is null) return null;

        JsonElement result;
        try {
            result = await rpc.RequestAsync("hooks/list", w => {
                w.WriteStartArray("cwds");
                w.WriteStringValue(cwd);
                w.WriteEndArray();
            }).ConfigureAwait(false);
        } catch (CodexAppServerRpc.CallFailed) {
            return null;
        }

        if (result.ValueKind != JsonValueKind.Object || result.Arr("data") is null) return null;

        var after = CodexTrustFingerprint.Of(paths);
        if (after is null || after != before) return null;

        var trust = CodexHookTrust.Classify(CodexHookTrust.ParseHooksList(result)) is CodexHookTrustDecision.Proceed
            ? HookTrustStates.Trusted
            : HookTrustStates.Untrusted;
        return new HookTrustObservation(trust, after);
    }
}
