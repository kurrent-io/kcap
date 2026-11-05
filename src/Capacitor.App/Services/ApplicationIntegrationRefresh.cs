using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>Persists the handoff so interrupted or failed integration repair retries on the next launch.</summary>
internal sealed class ApplicationIntegrationRefresh(ConfigRoot config, IProcessRunner runner) {
    internal const string FileName = "app-integration-refresh-pending";

    public void Schedule(string bundleRoot) {
        Directory.CreateDirectory(config.Directory);
        using var guard = config.AcquireLock(FileName);
        var staging = config.Path(FileName + "." + Guid.NewGuid().ToString("N"));
        try {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var stream = new FileStream(staging, options))
            using (var writer = new StreamWriter(stream))
                writer.Write(Path.GetFullPath(bundleRoot));
            File.Move(staging, config.Path(FileName), true);
        } finally { if (File.Exists(staging)) File.Delete(staging); }
    }

    public async Task<ProcessResult> RunAsync(string cli, CancellationToken ct) =>
        (await RunLockedAsync(cli, requireMarker: false, ct))!;

    public async Task<string?> RunPendingAsync(string? bundleRoot, CancellationToken ct) {
        try {
            if (bundleRoot is null || !File.Exists(config.Path(FileName))) return null;
            var cli = Path.Combine(bundleRoot, "Contents", "MacOS", "kcap");
            var result = await RunLockedAsync(cli, requireMarker: true, ct);
            if (result is null) return null;
            if (result.TimedOut || result.ExitCode != 0)
                throw new IOException(result.TimedOut ? "The integration refresh timed out." : result.Stderr.Trim());
            return null;
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Console.Error.WriteLine($"kcap: integration refresh failed: {ex}");
            return "Some agent integrations could not be refreshed. kcap will retry the next time it opens.";
        }
    }

    // Held for the whole refresh so a bundle-move run and an adoption run never overlap. With requireMarker, a
    // marker that is gone or names another bundle means someone else consumed it: null, nothing run.
    async Task<ProcessResult?> RunLockedAsync(string cli, bool requireMarker, CancellationToken ct) {
        Directory.CreateDirectory(config.Directory);
        var bundle = Path.GetFullPath(Path.Combine(cli, "..", "..", ".."));
        using var guard = config.AcquireLock(FileName);
        var pending = config.Path(FileName);
        var names = File.Exists(pending) && File.ReadAllText(pending) == bundle;
        if (requireMarker && !names) return null;
        var result = await runner.RunAsync(cli, ["refresh", "--app-migration"],
            new RunOptions(EnvOverlay: new Dictionary<string, string> { [ConfigRoot.ConfigDirEnvVar] = config.Directory },
                Timeout: TimeSpan.FromMinutes(3), CancelMode: CancelMode.KillTree), ct);
        ct.ThrowIfCancellationRequested();
        if (!result.TimedOut && result.ExitCode == 0 && names) File.Delete(pending);
        return result;
    }
}
