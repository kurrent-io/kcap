using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Cli.Core;

/// <summary>
/// Who posts the end of one process's run of a session: its SessionEnd hook, or the daemon recording it as a terminal
/// session, which posts it when that process exits with no hook in time. Both create this one file before they post,
/// and only the one that created it posts. It names the process by pid and start token, so a resumed run in another
/// process owes an end of its own. Beside the claims, in the directory hooks and the daemon share.
/// </summary>
public static class SessionEndRecord {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const string Extension = ".session-end";

    /// <summary>How long a record outlives its post. The daemon reads one at its next boot, however long it was down.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>Whether the caller posts the end of <paramref name="process"/>'s run of <paramref name="sessionId"/>: false
    /// only when someone created the record first. One that can't be written for another reason leaves the caller to post,
    /// at the risk of a second end, rather than leave the session with none.</summary>
    public static bool TryClaim(WatcherPaths paths, string sessionId, ProcessIncarnation process, DateTimeOffset at) {
        var path = PathOf(paths, sessionId, process);

        try {
            Directory.CreateDirectory(paths.Directory);

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

            using var stream = new FileStream(path, options);
            stream.Write(Encoding.UTF8.GetBytes(at.ToString("O", CultureInfo.InvariantCulture)));

            return true;
        } catch (IOException) when (File.Exists(path)) {
            return false;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return true;
        }
    }

    /// <summary>Whether someone has claimed the end of <paramref name="process"/>'s run of <paramref name="sessionId"/>.</summary>
    public static bool Exists(WatcherPaths paths, string sessionId, ProcessIncarnation process) => File.Exists(PathOf(paths, sessionId, process));

    /// <summary>Deletes records past <see cref="Retention"/>, except a session's in <paramref name="pending"/>, whose end
    /// may not have been decided yet.</summary>
    public static void Prune(WatcherPaths paths, DateTimeOffset now, IEnumerable<string> pending) {
        var keep = pending.Select(session => $"{SessionIds.Canonical(session) ?? session}.").ToArray();

        try {
            foreach (var record in Directory.EnumerateFiles(paths.Directory, $"*{Extension}")) {
                try {
                    var name = Path.GetFileName(record);

                    if (!keep.Any(prefix => name.StartsWith(prefix, StringComparison.Ordinal)) && now - File.GetLastWriteTimeUtc(record) > Retention)
                        File.Delete(record);
                } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                    // The next prune has another go.
                }
            }
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // No directory, nothing to prune.
        }
    }

    // The token is hashed because its spelling is the platform's, and only its identity matters here.
    static string PathOf(WatcherPaths paths, string sessionId, ProcessIncarnation process) {
        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(process.StartToken)))[..16];

        return Path.Combine(paths.Directory, $"{SessionIds.Canonical(sessionId) ?? sessionId}.{process.Pid.ToString(CultureInfo.InvariantCulture)}-{token}{Extension}");
    }
}
