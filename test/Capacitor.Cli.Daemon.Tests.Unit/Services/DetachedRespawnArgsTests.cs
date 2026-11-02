using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class DetachedRespawnArgsTests {
    [Test]
    public async Task Appends_await_lock_once() {
        var args = DetachedRespawnStrategy.BuildChildArgs(["--name", "laptop", "--log-file", "/tmp/d.log"]);
        await Assert.That(args).IsEquivalentTo(new[] { "--name", "laptop", "--log-file", "/tmp/d.log", "--await-lock" });
    }

    [Test]
    public async Task Does_not_duplicate_existing_await_lock() {
        var args = DetachedRespawnStrategy.BuildChildArgs(["--name", "laptop", "--await-lock"]);
        await Assert.That(args.Count(a => a == "--await-lock")).IsEqualTo(1);
    }

    /// <summary>After a Windows npm update the running image sits in the trash and the new binary at the install
    /// path, so the successor must run the install path.</summary>
    [Test]
    public async Task The_successor_of_an_npm_install_runs_the_path_the_daemon_started_from() {
        using var tmp = new TempDir();
        tmp.CreateFile("npm/node_modules/@kurrent/kcap/bin/kcap.js", "");
        var launch = tmp.CreateFile("npm/node_modules/@kurrent/kcap-win32-x64/bin/kcap-daemon.exe", "new");

        await Assert.That(DetachedRespawnStrategy.SuccessorBinary(launch, File.Exists)).IsEqualTo(launch);
    }

    [Test]
    public async Task The_successor_of_a_script_install_runs_through_current() {
        using var tmp = new TempDir();
        tmp.CreateFile("kcap/install.json", "{}");
        var launch  = tmp.CreateFile(["kcap", "versions", "1.0.0", "bin", "kcap-daemon"], "old");
        var current = tmp.CreateFile(["kcap", "current", "bin", "kcap-daemon"], "new");

        await Assert.That(DetachedRespawnStrategy.SuccessorBinary(launch, File.Exists)).IsEqualTo(current);
    }

    [Test]
    public async Task No_launch_path_means_no_successor() =>
        await Assert.That(DetachedRespawnStrategy.SuccessorBinary(null, File.Exists)).IsNull();
}
