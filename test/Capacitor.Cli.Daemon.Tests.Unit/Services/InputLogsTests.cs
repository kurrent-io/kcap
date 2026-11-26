using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>What a boot reaps of the logs earlier daemons left: only a log nobody has written for a month, and never one
/// a source still needs</summary>
public class InputLogsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 23, 10, 0, 0, TimeSpan.Zero));

    InputLogs Logs() => new(Tmp.PathTo("input-logs"), Time, NullLogger.Instance);

    string Left(InputLogs logs, string key, TimeSpan age) {
        var path = logs.PathFor(key);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{}");
        File.SetLastWriteTimeUtc(path, (Time.GetUtcNow() - age).UtcDateTime);

        return path;
    }

    [Test]
    public async Task A_log_nobody_wrote_for_a_month_is_reaped_and_a_recent_one_is_kept() {
        var logs   = Logs();
        var stale  = Left(logs, "stale-session", InputLogs.Retention + TimeSpan.FromHours(1));
        var recent = Left(logs, "recent-session", InputLogs.Retention - TimeSpan.FromHours(1));

        logs.Reap([]);

        await Assert.That(File.Exists(stale)).IsFalse();
        await Assert.That(File.Exists(recent)).IsTrue();
    }

    /// <summary>A restored source's log is kept however old, including one whose key is hashed into its file name</summary>
    [Test]
    public async Task A_log_of_a_source_the_boot_restores_is_kept() {
        var logs     = Logs();
        var restored = Left(logs, "restored-session", TimeSpan.FromDays(90));
        var hashed   = Left(logs, "vendor/spelled:session", TimeSpan.FromDays(90));

        logs.Reap(["restored-session", "vendor/spelled:session"]);

        await Assert.That(File.Exists(restored)).IsTrue();
        await Assert.That(File.Exists(hashed)).IsTrue();
    }

    [Test]
    public async Task A_log_this_daemon_holds_open_is_kept() {
        var logs = Logs();
        var held = Left(logs, "held-session", TimeSpan.FromDays(90));
        logs.Open("held-session");

        logs.Reap([]);

        await Assert.That(File.Exists(held)).IsTrue();
    }

    [Test]
    public async Task A_daemon_with_no_logs_yet_reaps_nothing() {
        Logs().Reap([]);

        await Assert.That(Directory.Exists(Tmp.PathTo("input-logs"))).IsFalse();
    }
}
