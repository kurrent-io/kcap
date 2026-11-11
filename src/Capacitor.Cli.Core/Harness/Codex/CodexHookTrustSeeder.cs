using System.Text.Json;
using Capacitor.Cli.Core.Accounts;

namespace Capacitor.Cli.Core.Harness.Codex;

/// <summary>
/// Trusts kcap's own hooks in one Codex home exactly as Codex's trust prompt does: <c>hooks/list</c> for each hook's
/// key and current hash, then <c>config/batchWrite</c> upserting <c>{"&lt;key&gt;": {"trusted_hash": "&lt;hash&gt;"}}</c>
/// into <c>hooks.state</c>. Codex computes the hash and persists it in its own form, so kcap never derives or writes
/// a trust value itself. Only command hooks from this home's user <c>hooks.json</c> that invoke kcap are written,
/// and what landed is claimed in the ownership ledger, which is all uninstall removes.
/// </summary>
public sealed class CodexHookTrustSeeder(HarnessRegistry harnesses, UserHome home, TimeProvider time) {
    /// <summary>A cold first start of Codex's app-server on a fresh Windows machine has outlasted 15 s.</summary>
    internal TimeSpan Deadline { get; init; } = TimeSpan.FromSeconds(60);

    public async Task<CodexHookTrustSeed> SeedAsync(CodexPaths paths, CancellationToken ct) {
        if (!CodexHooksInstaller.ReferencesKcapHook(paths.UserHooksJson)) return CodexHookTrustSeed.NoHooks;
        if (harnesses.ResolveLaunchable(HarnessId.Codex) is not { } exe) return CodexHookTrustSeed.CodexUnavailable;

        var psi = CodexAppServer.StartInfo(exe, paths.Home, home);

        using var process = CliProcess.TryStart(psi, _ => { });
        if (process is null) return CodexHookTrustSeed.CodexUnavailable;

        // Drained so a chatty stderr cannot fill the pipe and stall the answer.
        process.BeginErrorReadLine();

        using var deadline = new CancellationTokenSource(Deadline, time);
        using var linked   = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var rpc = new CodexAppServerRpc(process.StandardInput, process.StandardOutput, linked.Token);

        var seed       = CodexHookTrustSeed.Failed;
        var unanswered = false;
        try {
            await rpc.InitializeAsync().ConfigureAwait(false);
            seed = await SeedAsync(rpc, psi.WorkingDirectory, paths).ConfigureAwait(false);
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            unanswered = true;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            // Codex refused or stopped, or the config lock or ledger could not be taken: nothing is claimed.
        } finally {
            if (unanswered) VendorProcess.Stop(process);
            else await CodexAppServer.ShutDownAsync(process, time).ConfigureAwait(false);
        }

        ct.ThrowIfCancellationRequested();

        return seed;
    }

    internal static async Task<CodexHookTrustSeed> SeedAsync(CodexAppServerRpc rpc, string cwd, CodexPaths paths) {
        var listed = KcapHooks(await ListAsync(rpc, cwd).ConfigureAwait(false), paths.UserHooksJson);
        if (listed.Count == 0) return CodexHookTrustSeed.NoHooks;

        var pending = listed.Where(h => IsReviewable(h.TrustStatus)).ToArray();
        if (pending.Any(h => string.IsNullOrEmpty(h.CurrentHash))) return CodexHookTrustSeed.Failed;

        var current = listed.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);
        var written = pending.Select(h => h.Key).ToHashSet(StringComparer.Ordinal);

        await CodexConfigToml.TrustHooksAsync(
                paths.ConfigToml, written, current, pending.Length == 0 ? null : () => WriteAsync(rpc, pending))
            .ConfigureAwait(false);

        if (pending.Length == 0) return CodexHookTrustSeed.AlreadyTrusted;

        var after = KcapHooks(await ListAsync(rpc, cwd).ConfigureAwait(false), paths.UserHooksJson);

        return after.Count > 0 && !after.Any(h => IsReviewable(h.TrustStatus))
            ? CodexHookTrustSeed.Trusted
            : CodexHookTrustSeed.Failed;
    }

    /// <summary>The hooks Codex lists from <paramref name="userHooksJson"/> in the user layer whose command is kcap's,
    /// one per key. A hook from any other file or layer, or a command that only mentions kcap, is never kcap's to
    /// trust.</summary>
    internal static IReadOnlyList<ListedHook> KcapHooks(JsonElement result, string userHooksJson) {
        var hooks = new List<ListedHook>();
        if (result.Arr("data") is not { } data) return hooks;

        foreach (var group in data.EnumerateArray()) {
            if (group.Arr("hooks") is not { } entries) continue;

            foreach (var hook in entries.EnumerateArray()) {
                if (hook.Str("source") != "user" || hook.Str("key") is not { Length: > 0 } key) continue;
                if (hook.Str("sourcePath") is not { } source || !CodexHooksTrustCheck.SameFile(source, userHooksJson)) continue;
                if (!CodexHooksParser.IsCapacitorCodexHookCommand(hook.Str("command"))) continue;
                if (hooks.Any(h => h.Key == key)) continue;

                hooks.Add(new ListedHook(key, hook.Str("currentHash"), hook.Str("trustStatus") ?? ""));
            }
        }

        return hooks;
    }

    /// <summary>The statuses Codex's own prompt offers to trust; anything else (<c>managed</c>, or a status this
    /// build does not know) is left alone.</summary>
    static bool IsReviewable(string status) => status is "untrusted" or "modified";

    static Task<JsonElement> ListAsync(CodexAppServerRpc rpc, string cwd) =>
        rpc.RequestAsync("hooks/list", w => {
            w.WriteStartArray("cwds");
            w.WriteStringValue(cwd);
            w.WriteEndArray();
        });

    static Task WriteAsync(CodexAppServerRpc rpc, IReadOnlyList<ListedHook> pending) =>
        rpc.RequestAsync("config/batchWrite", w => {
            w.WriteStartArray("edits");
            w.WriteStartObject();
            w.WriteString("keyPath", "hooks.state");
            w.WriteStartObject("value");
            foreach (var hook in pending) {
                w.WriteStartObject(hook.Key);
                w.WriteString("trusted_hash", hook.CurrentHash);
                w.WriteEndObject();
            }
            w.WriteEndObject();
            w.WriteString("mergeStrategy", "upsert");
            w.WriteEndObject();
            w.WriteEndArray();
            w.WriteBoolean("reloadUserConfig", true);
        });

    internal readonly record struct ListedHook(string Key, string? CurrentHash, string TrustStatus);
}
