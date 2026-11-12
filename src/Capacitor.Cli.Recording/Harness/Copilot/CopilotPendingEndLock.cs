using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>
/// One session's lock over its pending Copilot end, held by every writer of that record: the watcher that writes it, a
/// hook that discards it, and a sweep from reading it to promoting or dropping it. Writers run in separate processes.
/// <c>FileShare.None</c> is an exclusive lock on Windows and <c>flock(LOCK_EX)</c> on POSIX, the primitive
/// <see cref="WatcherLiveness.WithSpawnLock"/> and the daemon lock use, and the OS releases it with a process that dies
/// holding it. The lock file stays on disk: deleting a held one would let a waiter lock an orphaned inode.
/// </summary>
sealed class CopilotPendingEndLock : IDisposable {
    static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(20);

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    readonly FileStream _stream;

    CopilotPendingEndLock(FileStream stream) => _stream = stream;

    public static string FileFor(WatcherPaths paths, string sessionId) => Path.Join(paths.Directory, sessionId + ".copilot-pending-end.lock");

    /// <summary>Null when another holder kept it for all of <paramref name="wait"/>, or it could not be opened.</summary>
    public static CopilotPendingEndLock? TryAcquire(WatcherPaths paths, string sessionId, TimeSpan? wait = null) {
        var attempts = Math.Max(1, (int) Math.Ceiling((wait ?? DefaultWait) / Retry));

        for (var attempt = 1; ; attempt++) {
            try {
                Directory.CreateDirectory(paths.Directory);

                return new CopilotPendingEndLock(new FileStream(FileFor(paths, sessionId), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None));
            } catch (IOException) when (attempt < attempts) {
                Thread.Sleep(Retry);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return null;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
