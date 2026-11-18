namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>
/// One session's chat log, replayed again only once its length or write time changes: the log carries every tool
/// result, so a replay costs as much as the file is long. What a replay leaves out is reported once for the life of
/// the cache, not on every change.
/// </summary>
public sealed class VsCodeChatSessionCache(string path, string sessionId, Action<string> warn) {
    (long Length, DateTime WrittenAt)? _stamp;
    VsCodeChatSession?                 _current;
    bool                               _warned;

    public string Path => path;

    /// <summary>The last log read, which outlives the file going missing: VS Code moves it when a window changes
    /// workspace.</summary>
    public VsCodeChatSession? Current() {
        (long, DateTime) stamp;
        try {
            var info = new FileInfo(path);
            if (!info.Exists) {
                _stamp = null;
                return _current;
            }

            stamp = (info.Length, info.LastWriteTimeUtc);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return _current;
        }

        if (_stamp == stamp) return _current;

        // An unreadable log keeps the stamp unset, so the next poll tries again.
        if (VsCodeChatSessionLog.Read(path, sessionId) is not { } read) return _current;

        _stamp   = stamp;
        _current = read;

        if (read.Skipped.Count > 0 && !_warned) {
            _warned = true;
            warn($"VS Code chat log {path}: left out {read.Skipped.Count} part(s) of an unexpected shape, first: {read.Skipped[0]}");
        }

        return _current;
    }
}
