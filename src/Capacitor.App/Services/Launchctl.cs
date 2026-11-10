using System.Runtime.InteropServices;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Setup;

namespace Capacitor.App.Services;

/// <summary>The user's launchd domain, driven through <c>/bin/launchctl</c>. A bootout whose reply is lost may still
/// have unloaded the job, so callers confirm with <see cref="PrintAsync"/> rather than trust the exit code.</summary>
internal sealed partial class Launchctl(IProcessRunner runner, string? domain = null) {
    string Domain => domain ?? $"gui/{getuid()}";

    [LibraryImport("libc", EntryPoint = "getuid")]
    private static partial uint getuid();

    /// <summary>The job's state; a job launchd does not know is a result with a non-zero exit. Anything else that is not
    /// a clean answer throws, because a guess here would read an unknown state as an absent job.</summary>
    public async Task<ProcessResult> PrintAsync(string label, CancellationToken ct) {
        var result = await RunAsync(["print", $"{Domain}/{label}"], ct).ConfigureAwait(false);
        if (result.TimedOut || LaunchdPrint.Classify(result.ExitCode, result.Stdout, result.Stderr) == LabelProbe.Unknown)
            throw new IOException($"Could not determine the state of service '{label}'.");

        return result;
    }

    public Task<ProcessResult> BootoutAsync(string label, CancellationToken ct) => RunAsync(["bootout", $"{Domain}/{label}"], ct);

    public Task<ProcessResult> BootstrapAsync(string plistPath, CancellationToken ct) => RunAsync(["bootstrap", Domain, plistPath], ct);

    public Task<ProcessResult> KickstartAsync(string label, CancellationToken ct) => RunAsync(["kickstart", $"{Domain}/{label}"], ct);

    Task<ProcessResult> RunAsync(string[] args, CancellationToken ct) => runner.RunAsync("/bin/launchctl", args,
        new RunOptions(Timeout: TimeSpan.FromSeconds(10), CancelMode: CancelMode.KillTree), ct);
}
