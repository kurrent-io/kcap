namespace Capacitor.Cli.Core;

/// <summary>
/// One drain of a spool directory at a time, across processes. Drains read and rewrite the same temp
/// files, so two at once would post one entry twice, or an old start after an end. The OS releases the
/// lease with a process that dies holding it, so a killed drain never holds the next one back. The file
/// is never deleted: a hook still waiting on a deleted one would hold a lease nobody else could see.
/// </summary>
public sealed class SpoolDrainLease : IDisposable {
    public const string FileName = ".drain-lease";

    readonly FileStream _stream;

    SpoolDrainLease(FileStream stream) => _stream = stream;

    /// <summary>Null when another drain holds it; callers skip rather than wait, so a hook stays fast.</summary>
    public static SpoolDrainLease? TryTake(string dir) {
        var options = new FileStreamOptions { Mode = FileMode.OpenOrCreate, Access = FileAccess.ReadWrite, Share = FileShare.None };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;

        try {
            Directory.CreateDirectory(dir);
            return new SpoolDrainLease(new FileStream(Path.Combine(dir, FileName), options));
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    public void Dispose() => _stream.Dispose();
}
