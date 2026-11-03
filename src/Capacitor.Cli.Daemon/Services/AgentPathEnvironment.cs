using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>The daemon user's current terminal PATH, shared by discovery and child processes.</summary>
internal sealed class AgentPathEnvironment(IProcessRunner runner) {
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
        var path = await probe.TerminalPathAsync(ct).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(path)) return;

        // A daemon has no meaningful terminal working directory. Empty/relative entries must not
        // let a launched repository supply the executable. Keep nonexistent absolute directories:
        // an agent can be installed into one later without any other environment change.
        var absolute = string.Join(Path.PathSeparator,
            path.Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).Distinct(StringComparer.Ordinal));
        if (absolute.Length > 0) Environment.SetEnvironmentVariable("PATH", absolute);
    }
}
