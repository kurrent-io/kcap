using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>Asks <c>codex app-server</c>, started under the account's <c>CODEX_HOME</c>, for the signed-in
/// account (<c>account/read</c>) and its usage (<c>account/rateLimits/read</c>). Codex makes the authenticated
/// call; kcap never reads Codex's auth files. The two steps fail independently. Afterwards it asks
/// <c>hooks/list</c> whether Codex trusts kcap's hooks, as a side answer that never fails either step.</summary>
public sealed class CodexAccountProbe(HarnessRegistry harnesses, UserHome home, TimeProvider time) : IAccountProbe {
    internal TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(10);

    public async Task<ProbeResult> ProbeAsync(VendorAccount account, CancellationToken ct) {
        if (harnesses.ResolveLaunchable(HarnessId.Codex) is not { } exe) return Failed("codex not found on PATH or in the desktop app");

        var psi = CodexAppServer.StartInfo(exe, account.Directory, home);

        var paths             = AccountLayouts.Codex(home, account.Directory);
        var fingerprintBefore = CodexTrustFingerprint.Of(paths);

        string? startError = null;
        using var process = CliProcess.TryStart(psi, message => startError = message);
        if (process is null) return Failed(startError ?? "could not start codex app-server");

        string? lastStderr = null;
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) lastStderr = e.Data; };
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var rpc = new CodexAppServerRpc(process.StandardInput, process.StandardOutput, linked.Token);

        var            identityDone  = false;
        string?        identityError = null;
        string?        signIn        = null;
        JsonElement?   chatgpt       = null;
        string?        routingId     = null;
        var            limitsDone    = false;
        string?        limitsError   = null;
        string?        limitsNote    = null;
        JsonElement?   limits        = null;
        DateTimeOffset limitsAt      = default;
        HookTrustObservation? hookTrust = null;

        try {
            await rpc.InitializeAsync().ConfigureAwait(false);

            try {
                (signIn, chatgpt, routingId) = ReadAccount(await rpc.RequestAsync("account/read", _ => { }).ConfigureAwait(false));
            } catch (CodexAppServerRpc.CallFailed ex) {
                identityError = $"account/read: {ex.Message}";
            }
            identityDone = true;

            // Only a ChatGPT login has usage limits; asking without one fails every time.
            limitsNote = identityError is not null || chatgpt is not null ? null
                       : signIn == SignInStates.SignedOut                ? "not signed in"
                                                                         : "no ChatGPT account";
            if (limitsNote is null) {
                try {
                    limits   = await rpc.RequestAsync("account/rateLimits/read", w => w.WriteBoolean("excludeResetCreditDetails", true)).ConfigureAwait(false);
                    limitsAt = time.GetUtcNow();
                } catch (CodexAppServerRpc.CallFailed ex) {
                    limitsError = $"account/rateLimits/read: {ex.Message}";
                }
            }
            limitsDone = true;

            hookTrust = await CodexHookTrustProbe.ReadAsync(rpc, psi.WorkingDirectory, paths, fingerprintBefore).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            var timeout = $"codex app-server did not answer within {Deadline.TotalSeconds:0.#}s";
            if (!identityDone) identityError = timeout;
            if (!limitsDone) limitsError = timeout;
        } catch (Exception ex) when (ex is CodexAppServerRpc.CallFailed or IOException or ObjectDisposedException) {
            var failure = ex is CodexAppServerRpc.CallFailed
                ? $"codex app-server initialize: {ex.Message}"
                : $"codex app-server stopped: {CodexAppServerRpc.Preview(lastStderr ?? ex.Message)}";
            if (!identityDone) identityError = failure;
            if (!limitsDone) limitsError = failure;
        } finally {
            await VendorProcess.ShutDownAsync(process, time).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        var usageId     = limits is { } read ? CodexRateLimits.AccountIdFromRateLimitsRead(read) : null;
        var observation = limits is { } result ? CodexRateLimits.FromRateLimitsRead(result, limitsAt) : null;

        // The usage read can answer for another identity than the signed-in one; its numbers are not this login's.
        if (routingId is not null && usageId is not null && routingId != usageId) {
            observation = null;
            limitsError = "usage identity does not match the signed-in account";
        }

        var identity = identityError is null && chatgpt is { } a
            ? new AccountIdentity(a.Str("email"), null, null, a.Str("planType"), routingId ?? usageId)
            : null;

        return new ProbeResult(identityError is null ? signIn : null, identity, identityError, observation, limitsError, hookTrust, limitsNote);
    }

    /// <summary>The sign-in, the account object when it is a ChatGPT login (the only kind that names one), and
    /// the stable account id <c>workspaceRouting</c> carries.</summary>
    static (string? SignIn, JsonElement? ChatGpt, string? AccountId) ReadAccount(JsonElement result) {
        var accountId = result.Obj("workspaceRouting")?.Str("chatgptAccountId");
        if (result.Obj("account") is { } account)
            return (SignInStates.SignedIn, account.Str("type") == "chatgpt" ? account : null, accountId);

        return (result.Bool("requiresOpenaiAuth") == true ? SignInStates.SignedOut : null, null, null);
    }

    static ProbeResult Failed(string error) => new(null, null, error, null, error);
}
