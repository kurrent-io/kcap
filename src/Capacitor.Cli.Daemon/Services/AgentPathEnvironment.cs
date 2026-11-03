using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The daemon user's current terminal PATH, shared by discovery and child processes.</summary>
internal sealed class AgentPathEnvironment(IProcessRunner runner, TimeProvider? time = null) {
    static readonly TimeSpan TotalProbeTimeout = TimeSpan.FromSeconds(12);
    // Reusing the refreshed PATH as the next probe's input grows it on every poll when a startup
    // file prepends a directory. Always start the shell from the original service environment.
    readonly IReadOnlyDictionary<string, string> _probeEnvironment = new Dictionary<string, string> {
        ["PATH"] = Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin",
    };

    internal async Task RefreshAsync(CancellationToken ct) {
        // A fresh probe deliberately bypasses LoginShellProbe's lifetime cache. It executes as
        // this process's user; no sudo, identity switch, or credentials imported from shell output.
        var probe = new LoginShellProbe(runner,
            name => name == "PATH" ? _probeEnvironment["PATH"] : Environment.GetEnvironmentVariable(name), _probeEnvironment);
        // The runner bounds process execution, but a background child can keep its output pipes
        // open after the shell exits. Bound our complete wait too. The abandoned probe only
        // computes a value; after this deadline none of its eventual output can reach the setter.
        using var deadline = new CancellationTokenSource(TotalProbeTimeout, time ?? TimeProvider.System);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        string? path;
        try {
            path = await probe.TerminalPathAsync(cancellation.Token).ConfigureAwait(false);
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
