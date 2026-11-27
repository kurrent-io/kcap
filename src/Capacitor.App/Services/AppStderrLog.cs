using System.Reflection;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>
/// Copies what the app writes to stderr into <c>app.log</c> under the config root, one timestamped line at a time,
/// and still passes it to the original stderr. Launched from Finder, launchd or the Start menu, the app's stderr goes
/// nowhere, and most handled failures report only there. Owner-only, a link is refused, and the file is rotated once
/// past <see cref="MaxBytes"/>. Never throws: a log that cannot be written leaves stderr as it was.
/// </summary>
public sealed class AppStderrLog : TextWriter {
    public const string FileName = "app.log";
    internal const long MaxBytes = 2 * 1024 * 1024;
    internal const int MaxLine = 8 * 1024;
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static readonly string Version =
        typeof(AppStderrLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    readonly TextWriter _inner;
    readonly string _path;
    readonly TimeProvider _time;
    readonly Lock _gate = new();
    readonly StringBuilder _line = new();
    FileStream? _file;
    bool _closed;

    public AppStderrLog(TextWriter inner, string path, TimeProvider time) {
        _inner = inner;
        _path = path;
        _time = time;
        lock (_gate) Append($"--- started pid={Environment.ProcessId} version={Version}");
    }

    /// Routes <see cref="Console.Error"/> through a new log; the caller keeps it to <see cref="Stop"/> it.
    public static AppStderrLog Install(ConfigRoot config, TimeProvider time) {
        var log = new AppStderrLog(Console.Error, config.Path(FileName), time);
        Console.SetError(log);
        return log;
    }

    public string Path => _path;

    public override Encoding Encoding => _inner.Encoding;

    public override void Write(char value) {
        lock (_gate) {
            Pass(() => _inner.Write(value));
            Take(value);
        }
    }

    public override void Write(string? value) {
        if (value is null) return;
        lock (_gate) {
            Pass(() => _inner.Write(value));
            foreach (var c in value) Take(c);
        }
    }

    public override void Write(char[] buffer, int index, int count) {
        lock (_gate) {
            Pass(() => _inner.Write(buffer, index, count));
            for (var i = index; i < index + count; i++) Take(buffer[i]);
        }
    }

    public override void Flush() {
        lock (_gate) Pass(_inner.Flush);
    }

    /// Stops writing the file, as before an uninstall removes the config directory a write would recreate.
    public void Stop() {
        lock (_gate) {
            _closed = true;
            _line.Clear();
            CloseFile();
        }
    }

    protected override void Dispose(bool disposing) {
        if (disposing) Stop();
        base.Dispose(disposing);
    }

    static void Pass(Action write) {
        try { write(); } catch (Exception) { /* a closed stderr must not take the log down with it */ }
    }

    void Take(char c) {
        if (_closed) return;
        if (c == '\n') {
            Append(_line.ToString());
            _line.Clear();
        } else if (c != '\r' && _line.Length < MaxLine) {
            _line.Append(c);
        }
    }

    void Append(string line) {
        if (_closed) return;
        try {
            var bytes = Encoding.UTF8.GetBytes($"{_time.GetUtcNow().UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {line}\n");
            if (_file is null && !Open()) return;
            if (_file!.Length + bytes.Length > MaxBytes) {
                CloseFile();
                File.Move(_path, _path + ".1", overwrite: true);
                if (!Open()) return;
            }
            _file!.Write(bytes);
            _file.Flush();
        } catch (Exception) {
            CloseFile();
        }
    }

    bool Open() {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        // A link here would send everything the app reports to whatever file it names.
        if (new FileInfo(_path).LinkTarget is not null) {
            _closed = true;
            return false;
        }
        var options = new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.ReadWrite };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        _file = new FileStream(_path, options);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(_file.SafeFileHandle, OwnerOnly);
        return true;
    }

    void CloseFile() {
        try { _file?.Dispose(); } catch (Exception) { /* nothing left to report to */ }
        _file = null;
    }
}
