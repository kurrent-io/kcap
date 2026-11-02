using System.Globalization;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Services;

/// <summary>The process holding a daemon's <see cref="ServiceTxnLock"/>, as it recorded itself on taking it,
/// so a caller refused the lock can say what it is waiting for.</summary>
sealed record ServiceTxnHolder(int Pid, string? StartToken, DateTimeOffset Since, string Command) {
    /// <summary>The flags a holder's command names. Any other argument may carry a value nobody else
    /// should read, so it is left out.</summary>
    static readonly string[] NamedFlags = ["--verify", "--keep-agents", "--replace", "--force", "--system", "--no-start"];

    /// <summary>The program name, its leading verbs, and the <see cref="NamedFlags"/> it was given.</summary>
    internal static string CommandOf(IReadOnlyList<string> commandLine) {
        if (commandLine.Count == 0) return "kcap";

        var program = Path.GetFileNameWithoutExtension(commandLine[0]);
        var args    = commandLine.Skip(1).ToList();
        IEnumerable<string> words = [
            program.Length == 0 ? "kcap" : program,
            .. args.TakeWhile(a => !a.StartsWith('-')),
            .. args.Where(a => NamedFlags.Contains(a, StringComparer.Ordinal)).Distinct(StringComparer.Ordinal),
        ];

        return string.Join(' ', words);
    }

    internal string Serialize() => string.Join('\n',
        Pid.ToString(CultureInfo.InvariantCulture),
        StartToken ?? "",
        Since.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture),
        Command);

    /// <summary>Null for anything but the four lines <see cref="Serialize"/> writes, which a reader
    /// racing the write can see half of.</summary>
    internal static ServiceTxnHolder? Parse(string text) {
        var lines = text.Split('\n', 4);
        if (lines.Length != 4 || lines[3].Length == 0) return null;
        if (!int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid) || pid <= 0) return null;
        if (!long.TryParse(lines[2], NumberStyles.None, CultureInfo.InvariantCulture, out var since)) return null;

        return new ServiceTxnHolder(pid, lines[1].Length == 0 ? null : lines[1], DateTimeOffset.FromUnixTimeMilliseconds(since), lines[3]);
    }

    /// <summary>The holder in a few words: its command, pid and how long it has held the lock.</summary>
    internal string Describe(DateTimeOffset now) => $"`{Command}` (pid {Pid}, for {Elapsed(now - Since)})";

    static string Elapsed(TimeSpan span) {
        var seconds = Math.Max(0, (long) span.TotalSeconds);
        return seconds < 60 ? $"{seconds}s" : $"{seconds / 60}m {seconds % 60}s";
    }

    /// <summary>Records this process as the holder of the lock it has just taken. Returns the file to
    /// clear on release, or null when it could not be written: the lock is held either way.</summary>
    internal static string? Record(DaemonStore store, string daemonName, TimeProvider time) {
        var path = store.ServiceLockHolderPath(daemonName);

        try {
            var holder = new ServiceTxnHolder(
                Environment.ProcessId, ProcessStartToken.ForCurrent(), time.GetUtcNow(), CommandOf(Environment.GetCommandLineArgs()));
            File.WriteAllText(path, holder.Serialize());
            return path;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }
    }

    /// <summary>Called while the lock is still held, so it never deletes the record of the next holder.</summary>
    internal static void Clear(string? path) {
        if (path is null) return;

        try {
            File.Delete(path);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            // A record left behind names a process that no longer holds the lock; Read discards it once that process is gone.
        }
    }

    /// <summary>The recorded holder while its process is still running, else null.</summary>
    internal static ServiceTxnHolder? Read(DaemonStore store, string daemonName) {
        string text;

        try {
            using var stream = new FileStream(
                store.ServiceLockHolderPath(daemonName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            text = reader.ReadToEnd();
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            return null;
        }

        return Parse(text) is { } holder && IsRunning(holder) ? holder : null;
    }

    static bool IsRunning(ServiceTxnHolder holder) => holder.StartToken is { } token
        ? ProcessStartToken.Matches(holder.Pid, token) == true
        : ProcessStartToken.ForPid(holder.Pid) is not null;
}
