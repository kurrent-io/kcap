using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>Persists the handoff so interrupted or failed integration repair retries on the next launch.</summary>
internal sealed class ApplicationIntegrationRefresh(ConfigRoot config, IProcessRunner runner) {
    internal const string FileName = "app-integration-refresh-pending";
    static readonly TimeSpan RefreshTimeout = TimeSpan.FromMinutes(3);
    static readonly TimeSpan LockWait = RefreshTimeout + TimeSpan.FromSeconds(30);

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
        (await RunLockedAsync(cli, Path.Combine(cli, "..", "..", ".."), requireMarker: false, ct))!;

    /// <summary>The macOS app bundle at <paramref name="bundleRoot"/>, refreshed by its own CLI.</summary>
    public Task<string?> RunPendingAsync(string? bundleRoot, CancellationToken ct) =>
        RunPendingAsync(bundleRoot, bundleRoot is null ? null : Path.Combine(bundleRoot, "Contents", "MacOS", "kcap"), ct);

    /// <param name="bundleRoot">What <see cref="Schedule"/> named: the macOS app bundle, or the Velopack install's root.</param>
    /// <param name="cli">The kcap that install ships.</param>
    public async Task<string?> RunPendingAsync(string? bundleRoot, string? cli, CancellationToken ct) {
        try {
            if (bundleRoot is null || cli is null || !File.Exists(config.Path(FileName))) return null;
            var result = await RunLockedAsync(cli, bundleRoot, requireMarker: true, ct);
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
    async Task<ProcessResult?> RunLockedAsync(string cli, string bundleRoot, bool requireMarker, CancellationToken ct) {
        Directory.CreateDirectory(config.Directory);
        var bundle = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        // Off the calling (UI) thread: a second caller waits out the first refresh, then finds the marker gone.
        using var guard = await Task.Run(() => config.AcquireLock(FileName, LockWait), ct);
        var pending = config.Path(FileName);
        var names = File.Exists(pending)
            && string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(File.ReadAllText(pending))), bundle,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        if (requireMarker && !names) return null;
        var result = await runner.RunAsync(cli, ["refresh", "--app-migration"],
            new RunOptions(EnvOverlay: new Dictionary<string, string> { [ConfigRoot.ConfigDirEnvVar] = config.Directory },
                Timeout: RefreshTimeout, CancelMode: CancelMode.KillTree), ct);
        ct.ThrowIfCancellationRequested();
        if (!result.TimedOut && result.ExitCode == 0 && names) File.Delete(pending);
        return result;
    }
}
