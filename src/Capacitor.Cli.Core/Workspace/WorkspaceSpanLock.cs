namespace Capacitor.Cli.Core.Workspace;

/// <summary>
/// One session's span lock. Hooks run as separate processes, so every open and close takes it; the OS
/// releases it with a process that dies holding it. It covers span and spool files only and is never held
/// across a network call.
/// <para>A lock file is empty while live. Removing a session first writes a byte into it under the lock,
/// and a hook that was waiting on that file finds it retired and locks a fresh one. The retired file is
/// deleted only where that cannot hit a fresh one: on Unix by its holder before releasing it, on Windows
/// after release, where a file another hook holds cannot be deleted.</para>
/// </summary>
public sealed class WorkspaceSpanLock : IDisposable {
    static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(20);

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(1);

    const string FileName = ".lock";

    readonly FileStream _stream;
    readonly string     _sessionDir;

    WorkspaceSpanLock(FileStream stream, string sessionDir) {
        _stream     = stream;
        _sessionDir = sessionDir;
    }

    /// <summary>Null when another holder kept it for all of <paramref name="wait"/>.</summary>
    public static WorkspaceSpanLock? TryAcquire(string sessionDir, TimeSpan wait) {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var path     = Path.Combine(sessionDir, FileName);
        var attempts = Math.Max(1, (int) Math.Ceiling(wait / Retry));
        for (var attempt = 1; ; attempt++) {
            try {
                Directory.CreateDirectory(sessionDir);
                var stream = new FileStream(path, options);
                if (stream.Length == 0) return new WorkspaceSpanLock(stream, sessionDir);

                // Still at the path while this hook holds it: its retirer died before deleting it.
                if (!OperatingSystem.IsWindows() && IsRetired(path)) WorkspaceFiles.TryDelete(path);
                stream.Dispose();
                if (OperatingSystem.IsWindows()) WorkspaceFiles.TryDelete(path);
                if (attempt >= attempts) return null;
                Thread.Sleep(Retry);
            } catch (IOException) when (attempt < attempts) {
                Thread.Sleep(Retry);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return null;
            }
        }
    }

    /// <summary>Deletes the session's directory: its other files under the lock, then the retired lock file
    /// and the directory once released. Ends this hold. A file another hook adds afterwards keeps the
    /// directory, and is never deleted.</summary>
    public void RemoveSession() {
        try {
            _stream.WriteByte(1);
            _stream.Flush();
            foreach (var file in Directory.EnumerateFiles(_sessionDir))
                if (Path.GetFileName(file) != FileName) WorkspaceFiles.TryDelete(file);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Whatever is left stays for the prune.
        }

        var path = Path.Combine(_sessionDir, FileName);
        if (!OperatingSystem.IsWindows()) WorkspaceFiles.TryDelete(path);
        _stream.Dispose();
        if (OperatingSystem.IsWindows() && IsRetired(path)) WorkspaceFiles.TryDelete(path);
        try {
            Directory.Delete(_sessionDir);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // Not empty: a hook took a fresh lock or wrote a file meanwhile.
        }
    }

    static bool IsRetired(string path) {
        try {
            return new FileInfo(path).Length > 0;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    public void Dispose() => _stream.Dispose();
}
