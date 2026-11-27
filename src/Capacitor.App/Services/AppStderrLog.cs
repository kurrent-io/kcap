using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Capacitor.Cli.Core;
using Microsoft.Win32.SafeHandles;

namespace Capacitor.App.Services;

/// <summary>
/// Copies what the app writes to stderr into <c>app.log</c> under the config root, one timestamped line at a time,
/// and still passes it to the original stderr. Launched from Finder, launchd or the Start menu, the app's stderr goes
/// nowhere, and most handled failures report only there. Owner-only, one writer at a time, anything but a regular
/// file is refused, and the file is rotated once past <see cref="MaxBytes"/>. Never throws: a log that cannot be
/// written leaves stderr as it was.
/// </summary>
public sealed partial class AppStderrLog : TextWriter {
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
    bool _lineFull;

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
            _lineFull = false;
        } else if (c != '\r' && !_lineFull) {
            // A character past the cap, or a surrogate pair it would split, ends what the line keeps.
            _lineFull = _line.Length >= MaxLine || (char.IsHighSurrogate(c) && _line.Length == MaxLine - 1);
            if (!_lineFull) _line.Append(c);
        }
    }

    /// The longest prefix of <paramref name="line"/> that is at most <see cref="MaxLine"/> bytes of UTF-8.
    internal static string Capped(string line) {
        var bytes = 0;
        var end = 0;
        foreach (var rune in line.EnumerateRunes()) {
            if (bytes + rune.Utf8SequenceLength > MaxLine) break;
            bytes += rune.Utf8SequenceLength;
            end += rune.Utf16SequenceLength;
        }
        return line[..end];
    }

    void Append(string line) {
        if (_closed) return;
        try {
            var bytes = Encoding.UTF8.GetBytes($"{_time.GetUtcNow().UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {Capped(line)}\n");
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

    /// Opens the file for this process alone, or refuses it for good: when another process writes it, or when the
    /// path is a link or anything but a regular file, which would send what the app reports elsewhere or block.
    bool Open() {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
        var file = OperatingSystem.IsWindows() ? OpenOnWindows() : OpenOnUnix();
        if (file is null) {
            _closed = true;
            return false;
        }
        _file = file;
        return true;
    }

    [UnsupportedOSPlatform("windows")]
    FileStream? OpenOnUnix() {
        try {
            using (new FileStream(_path, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, UnixCreateMode = OwnerOnly })) { }
        } catch (IOException) { /* it exists, and is checked below */ }

        var fd = open(_path, UnixOpenFlags);
        if (fd < 0) return null;
        var file = new FileStream(new SafeFileHandle(fd, ownsHandle: true), FileAccess.Write, bufferSize: 0);
        if (!file.CanSeek || flock(fd, LockExclusive | LockNonBlocking) != 0) {
            file.Dispose();
            return null;
        }
        File.SetUnixFileMode(file.SafeFileHandle, OwnerOnly);
        file.Seek(0, SeekOrigin.End);
        return file;
    }

    [SupportedOSPlatform("windows")]
    FileStream? OpenOnWindows() {
        if (new FileInfo(_path).LinkTarget is not null) return null;
        FileStream file;
        try {
            file = new FileStream(_path, new FileStreamOptions { Mode = FileMode.Append, Access = FileAccess.Write, Share = FileShare.Read });
        } catch (IOException) {
            return null;
        }
        file.SetAccessControl(OwnerOnlyDirectory.OwnerOnlyFileSecurity());
        return file;
    }

    const int LockExclusive = 2;
    const int LockNonBlocking = 4;

    /// <c>O_WRONLY | O_NONBLOCK | O_NOFOLLOW | O_CLOEXEC</c>: a FIFO with no reader fails at once instead of blocking.
    static readonly int UnixOpenFlags = OperatingSystem.IsMacOS()
        ? 0x1 | 0x4 | 0x100 | 0x1000000
        : 0x1 | 0x800 | (RuntimeInformation.ProcessArchitecture is Architecture.Arm64 or Architecture.Arm ? 0x8000 : 0x20000) | 0x80000;

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int open(string path, int flags);

    [LibraryImport("libc", EntryPoint = "flock")]
    private static partial int flock(int fd, int operation);

    void CloseFile() {
        try { _file?.Dispose(); } catch (Exception) { /* nothing left to report to */ }
        _file = null;
    }
}
