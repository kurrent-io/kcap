namespace Capacitor.Cli.Core.Setup;

/// Puts the CLI's directory first on the user PATH, so it is searched ahead of a `kcap` launcher another user-level
/// install left on it. No elevation and nothing to clobber: that launcher stays where it is, so there is no conflict
/// state. Windows searches the machine PATH before any user entry, and a `kcap` there still comes first.
public sealed class WindowsUserPathInstaller(IUserPathStore store, ILoginShellProbe probe) : ICliPathInstaller {
    public string Disclosure =>
        "This adds this app's folder to your user PATH, so kcap works from any new terminal. " +
        "No administrator rights are needed.";

    public Task<IReadOnlyList<DisclosureSpan>> DescribeAsync(string target, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<DisclosureSpan>>(CliDirectory(target) is { } dir
            ? [new("It puts "), new(dir, Code: true), new(" on your user PATH. No administrator rights are needed.")]
            : [new(Disclosure)]);

    public ShimPreflight Preflight(string target) =>
        CliDirectory(target) is { } dir && Contains(store.Read(), dir) ? ShimPreflight.AlreadyInstalled : ShimPreflight.Installable;

    public async Task<ShimResult> InstallAsync(string target, CancellationToken ct) {
        var dir = CliDirectory(target);
        if (dir is null || dir.Contains(';') || dir.Contains('\n') || dir.Contains('\r'))
            return new ShimResult(ShimOutcome.Failed, $"The CLI's folder cannot be added to PATH: {target}") { PathIssue = ShimPathIssue.InvalidTarget };

        try {
            var current = store.Read();
            if (!Contains(current, dir)) store.Replace(Leading(current, dir));
        } catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException) {
            return new ShimResult(ShimOutcome.Failed, $"Could not update your user PATH: {ex.Message}") { PathIssue = ShimPathIssue.UserPathNotUpdated, Folder = dir };
        }

        var onPath = await probe.KcapOnPathAsync(ct, forceRefresh: true).ConfigureAwait(false);
        return onPath switch {
            true  => new ShimResult(ShimOutcome.Installed, null),
            false => new ShimResult(ShimOutcome.InstalledButNotOnPath,
                $"{dir} is on your user PATH, but kcap does not resolve from it yet. Open a new terminal and run kcap --version.") { PathIssue = ShimPathIssue.UserPathUnverified, Folder = dir },
            _     => new ShimResult(ShimOutcome.Failed, $"{dir} was added to your user PATH, but it could not be re-read to confirm.") { PathIssue = ShimPathIssue.UserPathUnverified, Folder = dir },
        };
    }

    /// Takes the CLI's folder back off the user PATH, keeping every other entry byte for byte.
    /// False when it was not there.
    public bool Remove(string target) {
        if (CliDirectory(target) is not { } dir || Without(store.Read(), dir) is not { } next) return false;
        store.Replace(next);
        return true;
    }

    /// Every existing entry is kept byte for byte, behind the new one.
    internal static string Leading(string? rawPath, string dir) =>
        string.IsNullOrEmpty(rawPath) ? dir : dir + ";" + rawPath.TrimStart(';');

    internal static string? Without(string? rawPath, string dir) {
        if (string.IsNullOrEmpty(rawPath)) return null;
        var entries = rawPath.Split(';');
        var kept = entries.Where(entry => !Matches(entry, dir)).ToArray();
        return kept.Length == entries.Length ? null : string.Join(';', kept);
    }

    static string? CliDirectory(string target) =>
        Path.IsPathFullyQualified(target) ? Path.GetDirectoryName(Path.GetFullPath(target)) : null;

    internal static bool Contains(string? rawPath, string dir) =>
        !string.IsNullOrEmpty(rawPath) && rawPath.Split(';').Any(entry => Matches(entry, dir));

    static bool Matches(string entry, string dir) =>
        entry.Trim().Length > 0
        && string.Equals(Normalize(Environment.ExpandEnvironmentVariables(entry.Trim().Trim('"'))), Normalize(dir), StringComparison.OrdinalIgnoreCase);

    static string Normalize(string dir) => dir.TrimEnd('\\', '/');
}
