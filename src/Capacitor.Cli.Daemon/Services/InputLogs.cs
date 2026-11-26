using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>One <see cref="InputLog"/> per recorded source, shared by the runtime that writes the session's messages
/// and the recorder that stamps its rows, under the daemon's owner-only state directory</summary>
internal sealed class InputLogs(string directory, TimeProvider time, ILogger logger) {
    readonly Lock _gate = new();
    readonly Dictionary<string, InputLog> _logs = new(StringComparer.Ordinal);

    /// <summary>The log for <paramref name="key"/>, loaded from its file when this daemon holds none yet</summary>
    public InputLog Open(string key) {
        lock (_gate) return OpenLocked(key);
    }

    /// <summary>The log a launch opened for <paramref name="key"/>, or one the last daemon left on disk. Null for a
    /// source no runtime of this daemon or the last one writes messages for</summary>
    public InputLog? Find(string key) {
        lock (_gate) return _logs.GetValueOrDefault(key) ?? (File.Exists(PathFor(key)) ? OpenLocked(key) : null);
    }

    InputLog OpenLocked(string key) {
        if (!_logs.TryGetValue(key, out var log)) _logs[key] = log = new InputLog(PathFor(key), time, logger, key);

        return log;
    }

    /// <summary>The source is finished, and with every line acknowledged its log has nothing left to answer</summary>
    public void Delete(string key) {
        lock (_gate) {
            if (_logs.Remove(key, out var log)) log.Retire();

            try {
                File.Delete(PathFor(key));
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                // A file left behind holds hashes only, and the next launch of the session resets it.
            }
        }
    }

    internal string PathFor(string key) => Path.Combine(directory, $"{FileName(key)}.json");

    // A key is a session id, which a vendor spells: anything that isn't plainly a file name is hashed into one.
    static string FileName(string key) =>
        key.Length is > 0 and <= 128 && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_')
            ? key
            : Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
}
