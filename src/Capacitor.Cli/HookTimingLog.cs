using System.Globalization;
using System.Text;

namespace Capacitor.Cli;

/// <summary>
/// One hook process's phase timings, a line per phase. Lines marked before <see cref="Open"/> wait
/// for it; after that each is appended at once, so a hook the agent kills at its timeout still leaves
/// every phase it finished. Appends with shared access: concurrent hooks write the same file.
/// </summary>
public sealed class HookTimingLog(TimeProvider time, DateTimeOffset? processStart, int pid, string invocation) {
    public const string FileName = "hook-timing.log";

    readonly Lock         _gate    = new();
    readonly List<string> _pending = [];
    long?                 _last;
    string?               _path;
    string?               _event;
    string?               _session;

    public void Open(string path) {
        lock (_gate) {
            if (_path is not null) return;

            _path = path;

            try { Directory.CreateDirectory(Path.GetDirectoryName(path)!); } catch { }

            foreach (var line in _pending) Append(path, line);
            _pending.Clear();
        }
    }

    public void Describe(string? eventName, string? sessionId) {
        lock (_gate) {
            _event   = eventName;
            _session = sessionId;
        }
    }

    public void Mark(string phase) {
        lock (_gate) {
            var now  = time.GetTimestamp();
            var step = _last is { } last ? time.GetElapsedTime(last, now) : TimeSpan.Zero;
            _last = now;

            var line = Format(phase, step);

            if (_path is null) _pending.Add(line);
            else Append(_path, line);
        }
    }

    string Format(string phase, TimeSpan step) {
        var wall  = time.GetUtcNow();
        var since = processStart is { } started ? $" @{Ms(wall - started)}ms" : "";

        return string.Create(CultureInfo.InvariantCulture,
            $"{wall:O} pid={pid} {Token(_event)} {Token(_session)} {phase} +{Ms(step)}ms{since} [{invocation}]");
    }

    // Payload text, so one line per mark holds only if nothing in it can break the line.
    static string Token(string? value) {
        if (string.IsNullOrEmpty(value)) return "-";

        var span = value.AsSpan(0, Math.Min(value.Length, 64));
        var safe = new char[span.Length];
        for (var i = 0; i < span.Length; i++)
            safe[i] = char.IsAsciiLetterOrDigit(span[i]) || span[i] is '-' or '_' ? span[i] : '_';

        return new string(safe);
    }

    static long Ms(TimeSpan span) => (long)Math.Round(span.TotalMilliseconds);

    static void Append(string path, string line) {
        try {
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
        } catch {
            // Diagnostics only: a log that cannot be written must never cost the hook.
        }
    }
}
