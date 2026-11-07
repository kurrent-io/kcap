using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.Cli.Core;

/// <summary>
/// Who posts the end of one process's run of a session: its SessionEnd hook, or the daemon recording it as a terminal
/// session, which posts it when that process exits with no hook in time. Each claims this file before it posts and marks
/// it posted once the end has reached the server or the spool. A claim never marked is taken over once it is older than
/// <see cref="TakeOver"/>, by the daemon's next boot too, and the server appends nothing for an end of a session it has
/// already ended, so a claimer killed before its post costs the session no end. It names the process by pid and start
/// token, so a resumed run in another process owes an end of its own. Beside the claims, in the directory hooks and the
/// daemon share.
/// </summary>
public static class SessionEndRecord {
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    const string Extension = ".session-end";

    const string Claimed = "claimed";
    const string Posted  = "posted";
    const string Settled = "settled";

    /// <summary>How long a record outlives its post. The daemon reads one at its next boot, however long it was down.</summary>
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// <summary>How long a claim waits to be marked posted before whoever comes next takes it over. Longer than a SessionEnd
    /// hook's whole budget and the daemon's post budget, 15 seconds each, so a claim this old has lost its claimer.</summary>
    public static readonly TimeSpan TakeOver = TimeSpan.FromSeconds(30);

    /// <summary>Whether the caller posts the end of <paramref name="process"/>'s run of <paramref name="sessionId"/>. A
    /// record that can't be read, a creator's own whose write failed included, is a claim nobody marked, and so is taken
    /// over; one that can't be written at all leaves the caller to post rather than leave the session with no end.</summary>
    public static SessionEndTurn TryClaim(WatcherPaths paths, string sessionId, ProcessIncarnation process, DateTimeOffset now) {
        var path = PathOf(paths, sessionId, process);

        try {
            Directory.CreateDirectory(paths.Directory);

            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;

            FileStream stream;

            try {
                stream = new FileStream(path, options);
            } catch (IOException) when (File.Exists(path)) {
                return TakeOverIfStale(path, now);
            }

            // Created, so the claim is this caller's whatever the write does.
            using (stream) stream.Write(Encode(Claimed, now));

            return SessionEndTurn.Yours;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return SessionEndTurn.Yours;
        }
    }

    /// <summary>Marks a claimed end as having reached the server or the spool, so nobody takes it over.</summary>
    public static void MarkPosted(WatcherPaths paths, string sessionId, ProcessIncarnation process, DateTimeOffset now) {
        var path = PathOf(paths, sessionId, process);

        try {
            if (File.Exists(path)) AtomicFile.Replace(path, Encode(Posted, now), OwnerOnly, followLink: false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Left a claim, it is taken over and posted again, which the server takes for the end it already has.
        }
    }

    /// <summary>Records that nobody posts the end of that run: the session went on in another process, or a hook that
    /// can't name its process is posting it. One already posted stays as it is.</summary>
    public static void Settle(WatcherPaths paths, string sessionId, ProcessIncarnation process, DateTimeOffset now) {
        var path = PathOf(paths, sessionId, process);

        try {
            if (Read(path) is (Posted, _)) return;

            Directory.CreateDirectory(paths.Directory);
            AtomicFile.Replace(path, Encode(Settled, now), OwnerOnly, followLink: false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // The end is then posted after all, which only a resumed session's late hook could make.
        }
    }

    /// <summary>Whether anyone has claimed, posted or settled the end of <paramref name="process"/>'s run.</summary>
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

    // Two takers can both find a claim stale and both post; the server appends the first and ignores the second.
    static SessionEndTurn TakeOverIfStale(string path, DateTimeOffset now) {
        switch (Read(path)) {
            case (Posted or Settled, _):                                 return SessionEndTurn.Done;
            case (Claimed, { } at) when (now - at).Duration() < TakeOver: return SessionEndTurn.Pending;
        }

        try {
            AtomicFile.Replace(path, Encode(Claimed, now), OwnerOnly, followLink: false);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Posting all the same beats leaving the session with no end.
        }

        return SessionEndTurn.Yours;
    }

    static (string? State, DateTimeOffset? At) Read(string path) {
        try {
            var lines = File.ReadAllText(path).Split('\n');

            return (lines[0], lines is [_, var at, ..] && DateTimeOffset.TryParse(at, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var when) ? when : null);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return (null, null);
        }
    }

    static byte[] Encode(string state, DateTimeOffset at) => Encoding.UTF8.GetBytes($"{state}\n{at.ToString("O", CultureInfo.InvariantCulture)}");

    // The token is hashed because its spelling is the platform's, and only its identity matters here.
    static string PathOf(WatcherPaths paths, string sessionId, ProcessIncarnation process) {
        var token = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(process.StartToken)))[..16];

        return Path.Combine(paths.Directory, $"{SessionIds.Canonical(sessionId) ?? sessionId}.{process.Pid.ToString(CultureInfo.InvariantCulture)}-{token}{Extension}");
    }
}
