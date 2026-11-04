using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The daemon user's current terminal PATH, shared by discovery and child processes.</summary>
internal sealed class AgentPathEnvironment(IProcessRunner runner, TimeProvider? time = null) {
    static readonly TimeSpan TotalProbeTimeout = TimeSpan.FromSeconds(12);
    readonly object _probeLock = new();
    Task<string?>? _pendingProbe;
    // Reusing the refreshed PATH as the next probe's input grows it on every poll when a startup
    // file prepends a directory. Always start the shell from the original service environment.
    readonly IReadOnlyDictionary<string, string> _probeEnvironment = new Dictionary<string, string> {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
    };

    internal async Task RefreshAsync(CancellationToken ct) {
        ct.ThrowIfCancellationRequested();
        Task<string?> probe;
        lock (_probeLock) {
            // A background child can retain the shell's pipes past our deadline. Keep one probe
            // alive until it drains, so repeated refreshes cannot accumulate tasks and handles.
            if (_pendingProbe is { IsCompleted: false }) return;
            probe = _pendingProbe = new LoginShellProbe(runner,
                name => name == "PATH" ? _probeEnvironment["PATH"] : Environment.GetEnvironmentVariable(name),
                _probeEnvironment).TerminalPathAsync(CancellationToken.None);
        }
        using var deadline = new CancellationTokenSource(TotalProbeTimeout, time ?? TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        string? path;
        try {
            path = await probe.WaitAsync(cancellation.Token).ConfigureAwait(false);
            cancellation.Token.ThrowIfCancellationRequested();
        } catch (OperationCanceledException) when (deadline.IsCancellationRequested && !ct.IsCancellationRequested) {
            return;
        }
        if (string.IsNullOrWhiteSpace(path)) return;

        // A daemon has no meaningful terminal working directory. Empty/relative entries must not
        // let a launched repository supply the executable. Keep nonexistent absolute directories:
        // an agent can be installed into one later without any other environment change.
        var absolute = string.Join(Path.PathSeparator,
            path.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal));
        if (absolute.Length > 0) Environment.SetEnvironmentVariable("PATH", absolute);
    }
}
