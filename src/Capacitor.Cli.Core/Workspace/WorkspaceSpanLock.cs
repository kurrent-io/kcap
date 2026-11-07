namespace Capacitor.Cli.Core.Workspace;

/// <summary>One session's span lock. Hooks run as separate processes, so every open and close takes it;
/// the OS releases it with a process that dies holding it. It covers span and spool files only and is
/// never held across a network call.</summary>
public sealed class WorkspaceSpanLock : IDisposable {
    static readonly TimeSpan Retry = TimeSpan.FromMilliseconds(20);

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(1);

    readonly FileStream _stream;

    WorkspaceSpanLock(FileStream stream) => _stream = stream;

    /// <summary>Null when another holder kept it for all of <paramref name="wait"/>.</summary>
    public static WorkspaceSpanLock? TryAcquire(string sessionDir, TimeSpan wait) {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        var attempts = Math.Max(1, (int) Math.Ceiling(wait / Retry));
        for (var attempt = 1; ; attempt++) {
            try {
                Directory.CreateDirectory(sessionDir);
                return new WorkspaceSpanLock(new FileStream(Path.Combine(sessionDir, ".lock"), options));
            } catch (IOException) when (attempt < attempts) {
                Thread.Sleep(Retry);
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                return null;
            }
        }
    }

    public void Dispose() => _stream.Dispose();
}
