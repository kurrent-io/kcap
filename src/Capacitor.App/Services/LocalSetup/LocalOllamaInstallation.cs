using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services.LocalSetup;

internal sealed class LocalOllamaInstallation(
        ILoginShellProbe? shellProbe, Func<bool> registeredApplication, Func<string?, bool> onPath,
        Func<bool> fixedPaths, TimeProvider time) {
    internal static readonly TimeSpan LookupTimeout = TimeSpan.FromSeconds(3);

    public async Task<bool> CheckAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        using var lookup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = lookup.Token;
        try {
            // Launch Services and shell startup can block before returning a task.
            if (await Task.Run(async () => {
                if (ApplicationInstalled()) return true;
                token.ThrowIfCancellationRequested();
                var path = shellProbe is null ? null : await shellProbe.TerminalPathAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                return onPath(path);
            }, token).WaitAsync(LookupTimeout, time, ct).ConfigureAwait(false)) {
                ct.ThrowIfCancellationRequested();
                return true;
            }
        } catch (Exception) when (!ct.IsCancellationRequested) {
            // A lookup failure must still allow a known install location to answer.
        } finally {
            lookup.Cancel();
        }

        ct.ThrowIfCancellationRequested();
        var installed = await Task.Run(fixedPaths, ct).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();
        return installed;
    }

    bool ApplicationInstalled() {
        try { return registeredApplication(); }
        catch (Exception) { return false; }
    }
}
