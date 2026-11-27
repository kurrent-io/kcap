using Capacitor.App.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit.Services;

public class AppStderrLogTests {
    [TempDir] public required TempDir Tmp { get; init; }

    static readonly DateTimeOffset Now = new(2026, 11, 26, 18, 30, 0, TimeSpan.Zero);

    (AppStderrLog Log, StringWriter Inner, string Path) Make() {
        var inner = new StringWriter();
        var path = Tmp.PathTo($"config/{AppStderrLog.FileName}");
        return (new AppStderrLog(inner, path, new FakeTimeProvider(Now)), inner, path);
    }

    [Test]
    public async Task Every_line_reaches_both_stderr_and_the_file_with_a_timestamp() {
        var (log, inner, path) = Make();
        using (log) {
            log.WriteLine("Desktop notifications: posted");
            log.Write("partial ");
            log.Write("line\r\n".ToCharArray(), 0, 6);
            log.Write("unterminated");
        }

        await Assert.That(inner.ToString()).IsEqualTo($"Desktop notifications: posted{Environment.NewLine}partial line\r\nunterminated");
        var lines = await File.ReadAllLinesAsync(path);
        await Assert.That(lines[0]).StartsWith("2026-11-26T18:30:00.000Z --- started pid=");
        await Assert.That(lines.Skip(1)).IsEquivalentTo([
            "2026-11-26T18:30:00.000Z Desktop notifications: posted",
            "2026-11-26T18:30:00.000Z partial line",
        ]);
    }

    [Test]
    public async Task The_file_is_owner_only() {
        if (OperatingSystem.IsWindows()) return;
        var (log, _, path) = Make();
        using (log) log.WriteLine("x");

        await Assert.That(File.GetUnixFileMode(path)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task A_full_file_rotates_to_one_previous_copy() {
        var path = Tmp.CreateFile($"config/{AppStderrLog.FileName}", new string('x', (int)AppStderrLog.MaxBytes));
        using (var log = new AppStderrLog(new StringWriter(), path, new FakeTimeProvider(Now))) log.WriteLine("after");

        await Assert.That(new FileInfo(path + ".1").Length).IsEqualTo(AppStderrLog.MaxBytes);
        await Assert.That((await File.ReadAllLinesAsync(path)).Last()).EndsWith(" after");
    }

    [Test]
    public async Task A_link_in_place_of_the_file_is_never_written_through() {
        if (OperatingSystem.IsWindows()) return;
        var target = Tmp.CreateFile("elsewhere.txt", "");
        var path = Tmp.PathTo($"config/{AppStderrLog.FileName}");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.CreateSymbolicLink(path, target);
        var inner = new StringWriter();
        using (var log = new AppStderrLog(inner, path, new FakeTimeProvider(Now))) log.WriteLine("secret");

        await Assert.That(await File.ReadAllTextAsync(target)).IsEmpty();
        await Assert.That(inner.ToString()).Contains("secret");
    }

    [Test]
    public async Task Stopped_it_writes_nothing_more_to_the_file_but_still_to_stderr() {
        var (log, inner, path) = Make();
        using (log) {
            log.Stop();
            File.Delete(path);
            log.WriteLine("after uninstall");
        }

        await Assert.That(File.Exists(path)).IsFalse();
        await Assert.That(inner.ToString()).Contains("after uninstall");
    }

    [Test]
    public async Task An_overlong_line_is_cut_at_the_limit() {
        var (log, _, path) = Make();
        using (log) log.WriteLine(new string('y', AppStderrLog.MaxLine + 100));

        var line = (await File.ReadAllLinesAsync(path)).Last();
        await Assert.That(line.Count(c => c == 'y')).IsEqualTo(AppStderrLog.MaxLine);
    }
}
