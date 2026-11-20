using System.Diagnostics;
using System.Globalization;

namespace Capacitor.App.Services;

/// <summary>
/// Start-up milestones in the lifecycle log, each written at most once per process and stamped with
/// <c>since_start_ms</c>, the milliseconds since the operating system started the process, so the
/// runtime's own start-up counts too. A process whose start time cannot be read omits the stamp.
/// </summary>
public sealed class StartupMarks(LifecycleLog log, TimeProvider time, DateTimeOffset? processStart) {
    public const string SinceStartField = "since_start_ms";

    readonly HashSet<string> _written = new(StringComparer.Ordinal);

    /// The kernel's record of when this process started: exact on macOS and Windows, to the clock tick on Linux.
    public static DateTimeOffset? CurrentProcessStart() {
        try {
            using var process = Process.GetCurrentProcess();
            return new DateTimeOffset(process.StartTime.ToUniversalTime(), TimeSpan.Zero);
        } catch (Exception ex) when (ex is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception) {
            return null;
        }
    }

    public DateTimeOffset Now => time.GetUtcNow();

    public bool Mark(string evt, params (string Key, string? Value)[] fields) => Mark(evt, time.GetUtcNow(), fields);

    /// <param name="at">When the milestone happened, for a caller that observed it on a thread it must not write from.</param>
    public bool Mark(string evt, DateTimeOffset at, params (string Key, string? Value)[] fields) {
        lock (_written) {
            if (!_written.Add(evt)) return false;
        }
        var stamp = processStart is { } start ? Milliseconds(at - start) : null;
        log.Write(evt, [(SinceStartField, stamp), .. fields]);
        return true;
    }

    public static string Milliseconds(TimeSpan span) =>
        ((long)Math.Max(0, span.TotalMilliseconds)).ToString(CultureInfo.InvariantCulture);
}
