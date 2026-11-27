using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services;

public class AppStderrLogTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly DateTimeOffset Now = new(2026, 11, 26, 18, 30, 0, TimeSpan.Zero);

    string Folder => Tmp.PathTo($"config/{AppStderrLog.FolderName}");
    string LogPath => Path.Combine(Folder, AppStderrLog.FileName);

    (AppStderrLog Log, StringWriter Inner) Make(FakeTimeProvider? time = null) {
        var inner = new StringWriter();
        return (new AppStderrLog(inner, Folder, time ?? new FakeTimeProvider(Now)), inner);
    }

    async Task<string[]> Logged() => (await File.ReadAllLinesAsync(LogPath)).Select(l => l.Split(' ', 2)[1]).ToArray();

    [Test]
    public async Task Every_line_reaches_both_stderr_and_the_file_with_a_timestamp() {
        var (log, inner) = Make();
        using (log) {
            log.WriteLine("Desktop notifications: posted");
            log.Write("partial ");
            log.Write("line\r\n".ToCharArray(), 0, 6);
            log.Write("unterminated");
        }

        await Assert.That(inner.ToString()).IsEqualTo($"Desktop notifications: posted{Environment.NewLine}partial line\r\nunterminated");
        var lines = await File.ReadAllLinesAsync(LogPath);
        await Assert.That(lines[0]).StartsWith("2026-11-26T18:30:00.000Z --- started pid=");
        await Assert.That(lines.Skip(1)).IsEquivalentTo([
            "2026-11-26T18:30:00.000Z Desktop notifications: posted",
            "2026-11-26T18:30:00.000Z partial line",
        ]);
    }

    [Test]
    public async Task The_folder_and_file_are_owner_only() {
        var (log, _) = Make();
        using (log) log.WriteLine("x");

        if (OperatingSystem.IsWindows()) {
            await Assert.That(OwnerOnlyDirectory.Violation(Folder)).IsNull();
            return;
        }
        await Assert.That(File.GetUnixFileMode(Folder)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        await Assert.That(File.GetUnixFileMode(LogPath)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task A_folder_other_accounts_can_open_is_never_written() {
        if (OperatingSystem.IsWindows()) return;
        Directory.CreateDirectory(Folder);
        File.SetUnixFileMode(Folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        var (log, inner) = Make();
        using (log) log.WriteLine("secret");

        await Assert.That(File.Exists(LogPath)).IsFalse();
        await Assert.That(inner.ToString()).Contains("secret");
    }

    [Test]
    public async Task A_second_process_leaves_the_file_to_the_first() {
        var (first, _) = Make();
        var (second, secondInner) = Make();
        using (first) {
            first.WriteLine("one");
            using (second) second.WriteLine("from the second");
            first.WriteLine("two");
        }

        await Assert.That((await Logged()).Skip(1)).IsEquivalentTo(["one", "two"]);
        await Assert.That(secondInner.ToString()).Contains("from the second");
    }

    [Test]
    public async Task A_copy_that_replaces_the_app_takes_the_file_over_once_the_app_has_gone() {
        var time = new FakeTimeProvider(Now);
        var (first, _) = Make();
        var (second, _) = Make(time);
        using (second) {
            first.WriteLine("from the first");
            first.Dispose();
            second.WriteLine("too soon");
            time.Advance(AppStderrLog.RetryAfter);
            second.WriteLine("after the handoff");
        }

        var logged = await Logged();
        await Assert.That(logged.Where(l => !l.StartsWith("--- started", StringComparison.Ordinal))).IsEquivalentTo(["from the first", "after the handoff"]);
        await Assert.That(logged.Count(l => l.StartsWith("--- started", StringComparison.Ordinal))).IsEqualTo(2);
    }

    [Test]
    public async Task A_full_file_rotates_to_one_previous_copy() {
        using (Make().Log) { }
        await File.WriteAllTextAsync(LogPath, new string('x', (int)AppStderrLog.MaxBytes));
        using (var log = Make().Log) log.WriteLine("after");

        await Assert.That(new FileInfo(LogPath + ".1").Length).IsEqualTo(AppStderrLog.MaxBytes);
        await Assert.That((await Logged()).Last()).IsEqualTo("after");
    }

    [Test]
    public async Task Stopped_it_writes_nothing_more_to_the_file_but_still_to_stderr() {
        var (log, inner) = Make();
        using (log) {
            log.Stop();
            Directory.Delete(Folder, recursive: true);
            log.WriteLine("after uninstall");
        }

        await Assert.That(Directory.Exists(Folder)).IsFalse();
        await Assert.That(inner.ToString()).Contains("after uninstall");
    }

    [Test]
    public async Task An_overlong_line_is_cut_at_the_limit() {
        var (log, _) = Make();
        using (log) log.WriteLine(new string('y', AppStderrLog.MaxLine + 100));

        await Assert.That((await Logged()).Last()).IsEqualTo(new string('y', AppStderrLog.MaxLine));
    }

    [Test]
    [Arguments("é", 2)]
    [Arguments("😀", 4)]
    public async Task The_limit_counts_utf8_bytes_and_never_splits_a_character(string character, int bytes) {
        var (log, _) = Make();
        using (log) log.WriteLine("y" + string.Concat(Enumerable.Repeat(character, AppStderrLog.MaxLine)));

        await Assert.That((await Logged()).Last()).IsEqualTo("y" + string.Concat(Enumerable.Repeat(character, (AppStderrLog.MaxLine - 1) / bytes)));
    }
}
