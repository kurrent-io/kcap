using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.App.Services;

/// <summary>
/// Copies what the app writes to stderr into <c>desktop-logs/app.log</c> under the config root, one timestamped line at a
/// time, and still passes it to the original stderr. Launched from Finder, launchd or the Start menu, the app's stderr
/// goes nowhere, and most handled failures report only there. The folder is owner-only, so no other account can read
/// the log or put anything in its place. One process writes it at a time, holding a lock file for as long as it does,
/// and rotates it once past <see cref="MaxBytes"/>. Never throws: a log that cannot be written leaves stderr as it was.
/// </summary>
public sealed partial class AppStderrLog : TextWriter {
    /// Not the CLI's <c>logs</c>, which holds watcher logs and is not owner-only.
    public const string FolderName = "desktop-logs";
    public const string FileName = "app.log";
    internal const long MaxBytes = 2 * 1024 * 1024;
    internal const int MaxLine = 8 * 1024;
    internal static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);
    const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    static readonly string Version =
        typeof(AppStderrLog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "?";

    readonly TextWriter _inner;
    readonly string _folder;
    readonly string _path;
    readonly TimeProvider _time;
    readonly Lock _gate = new();
    readonly StringBuilder _line = new();
    FileStream? _writerLock;
    FileStream? _file;
    DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;
    bool _headerWritten;
    bool _closed;
    bool _lineFull;

    /// <param name="folder">Made owner-only, or refused when it cannot be.</param>
    public AppStderrLog(TextWriter inner, string folder, TimeProvider time) {
        _inner = inner;
        _folder = folder;
        _path = System.IO.Path.Combine(folder, FileName);
        _time = time;
        lock (_gate) Append(null);
    }

    /// Routes <see cref="Console.Error"/> through a new log; the caller keeps it to <see cref="Stop"/> it.
    public static AppStderrLog Install(ConfigRoot config, TimeProvider time) {
        var log = new AppStderrLog(Console.Error, FolderIn(config), time);
        Console.SetError(log);
        return log;
    }

    internal static string FolderIn(ConfigRoot config) => config.Path(FolderName);

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
            Release();
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

    /// Appends one line, or this run's header when <paramref name="line"/> is null, which it also writes first
    /// whenever it takes over the file from another process.
    void Append(string? line) {
        if (_closed || !Own()) return;
        try {
            if (!_headerWritten) {
                AppendToFile($"--- started pid={Environment.ProcessId} version={Version}");
                _headerWritten = true;
            }
            if (line is not null) AppendToFile(Capped(line));
        } catch (Exception) {
            Release();
        }
    }

    void AppendToFile(string line) {
        var bytes = Encoding.UTF8.GetBytes($"{_time.GetUtcNow().UtcDateTime:yyyy-MM-ddTHH:mm:ss.fffZ} {line}\n");
        _file ??= OpenFile();
        if (_file.Length + bytes.Length > MaxBytes) {
            CloseFile();
            File.Move(_path, _path + ".1", overwrite: true);
            _file = OpenFile();
        }
        _file.Write(bytes);
        _file.Flush();
    }

    /// Takes the lock file another process may hold, retrying no more often than <see cref="RetryAfter"/>: a copy
    /// started to replace this one overlaps it for a moment, and takes the log over once it has gone.
    bool Own() {
        if (_writerLock is not null) return true;
        var now = _time.GetUtcNow();
        if (now < _nextAttempt) return false;
        _nextAttempt = now + RetryAfter;
        try {
            OwnerOnlyDirectory.CreateVerified(_folder);
            if (OperatingSystem.IsMacOS()) DropExtendedAcl(_folder);
            _writerLock = new FileStream(System.IO.Path.Combine(_folder, FileName + ".lock"), Options(FileMode.OpenOrCreate, FileShare.None));
            _headerWritten = false;
            return true;
        } catch (Exception) {
            return false;
        }
    }

    FileStream OpenFile() {
        var file = new FileStream(_path, Options(FileMode.Append, FileShare.Read));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file.SafeFileHandle, OwnerOnly);
        return file;
    }

    /// The mode bits pass for owner-only while an entry inherited from the config root still lets others in.
    [SupportedOSPlatform("macos")]
    static void DropExtendedAcl(string folder) {
        var empty = acl_init(0);
        if (empty == 0) throw new IOException("acl_init failed");
        try {
            if (acl_set_file(folder, AclTypeExtended, empty) != 0) throw new IOException($"cannot clear the ACL on {folder}");
        } finally {
            _ = acl_free(empty);
        }
    }

    const int AclTypeExtended = 0x100;

    [LibraryImport("libc", EntryPoint = "acl_init")]
    private static partial nint acl_init(int count);

    [LibraryImport("libc", EntryPoint = "acl_set_file", StringMarshalling = StringMarshalling.Utf8)]
    private static partial int acl_set_file(string path, int type, nint acl);

    [LibraryImport("libc", EntryPoint = "acl_free")]
    private static partial int acl_free(nint obj);

    static FileStreamOptions Options(FileMode mode, FileShare share) {
        var options = new FileStreamOptions { Mode = mode, Access = FileAccess.Write, Share = share };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = OwnerOnly;
        return options;
    }

    void Release() {
        CloseFile();
        try { _writerLock?.Dispose(); } catch (Exception) { /* nothing left to report to */ }
        _writerLock = null;
    }

    void CloseFile() {
        try { _file?.Dispose(); } catch (Exception) { /* nothing left to report to */ }
        _file = null;
    }
}
