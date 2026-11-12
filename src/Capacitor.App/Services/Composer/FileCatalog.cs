using Capacitor.Cli.Core;

namespace Capacitor.App.Services.Composer;

/// The files a composer's @ offers, read on this machine.
public sealed class FileCatalog(IProcessRunner runner, UserHome home, CancellationToken shutdown) {
    static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    const int OutputLimit = 32 * 1024 * 1024;

    /// Tracked and untracked files, gitignore respected; empty when git fails or the listing is too large.
    public Task<IReadOnlyList<string>> InRepoAsync(string directory) => Task.Run(async () => {
        ProcessResult result;
        try {
            result = await runner.RunBoundedAsync("git", ["-C", directory, "ls-files", "-z", "--cached", "--others", "--exclude-standard"],
                new RunOptions(Timeout: Timeout, CancelMode: CancelMode.KillTree), OutputLimit, shutdown).ConfigureAwait(false);
        } catch (Exception e) when (e is not OperationCanceledException) {
            return [];
        }
        if (result.TimedOut || result.Oversized || result.ExitCode != 0) return [];

        return (IReadOnlyList<string>)[.. result.Stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)];
    });

    /// One folder's entries, folders first with a trailing "/". <paramref name="folder"/> is relative to
    /// <paramref name="directory"/> unless rooted or under "~/".
    public Task<IReadOnlyList<string>> InFolderAsync(string directory, string folder) => Task.Run(() => {
        var path = folder.StartsWith("~/", StringComparison.Ordinal) ? Path.Combine(home.Path, folder[2..]) : Path.Combine(directory, folder);
        try {
            return (IReadOnlyList<string>)[
                .. new DirectoryInfo(path).EnumerateFileSystemInfos()
                    .OrderBy(entry => entry is FileInfo)
                    .ThenBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(entry => entry is DirectoryInfo ? $"{entry.Name}/" : entry.Name),
            ];
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException) {
            return [];
        }
    });
}
