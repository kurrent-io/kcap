using System.Runtime.InteropServices;
using TUnit.Core.Enums;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.Cli.Core.Tests.Unit.LocalEdition;

public class LocalClientCopiesPlatformTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Answers(string version) : IProcessRunner {
        public List<string> Ran { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Ran.Add(fileName);

            return Task.FromResult(new ProcessResult(0, $"kcap {version}\n", "", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine,
            CancellationToken ct) => throw new NotSupportedException();
    }

    [Test]
    public async Task On_a_mac_the_app_bundles_in_both_applications_folders() {
        var home = Tmp.PathTo("home");

        await Assert.That(LocalClientCopies.AppClis(new UserHome(home), OSPlatform.OSX)).IsEquivalentTo([
            Path.Combine("/Applications", "kcap.app", "Contents", "MacOS", "kcap"),
            Path.Combine(home, "Applications", "kcap.app", "Contents", "MacOS", "kcap"),
        ]);
    }

    [Test]
    public async Task On_windows_the_apps_per_user_install() {
        var home = Tmp.PathTo("home");

        await Assert.That(LocalClientCopies.AppClis(new UserHome(home), OSPlatform.Windows)).IsEquivalentTo([
            Path.Combine(home, "AppData", "Local", "kcap", "current", "bin", "kcap.exe"),
            Path.Combine(home, "AppData", "Local", "kcap", "current", "kcap.exe"),
        ]);
    }

    [Test]
    public async Task On_linux_no_app_copy() {
        await Assert.That(LocalClientCopies.AppClis(new UserHome(Tmp.PathTo("home")), OSPlatform.Linux)).IsEmpty();
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task An_older_windows_app_install_is_found_as_the_apps_copy() {
        var app    = Tmp.CreateExecutable(@"AppData\Local\kcap\current\bin\kcap.exe", "");
        var runner = new Answers("1.2.0");

        var copies = new LocalClientCopies(runner, searchPath: null, self: null, LocalClientCopies.AppClis(new UserHome(Tmp.Path))).Find();

        await Assert.That(copies).IsEquivalentTo([new ClientCopy(ClientCopyKind.DesktopApp, app, "1.2.0")]);
    }

    /// The running kcap and the app's copy are one file however PATH or the profile cases it.
    [Test]
    [RunOn(OS.Windows)]
    public async Task On_windows_the_running_kcap_in_another_casing_is_not_asked() {
        var app    = Tmp.CreateExecutable(@"AppData\Local\kcap\current\bin\kcap.exe", "");
        var runner = new Answers("1.2.0");

        var copies = new LocalClientCopies(runner, Path.GetDirectoryName(app), self: app.ToUpperInvariant(), [app]).Find();

        await Assert.That(copies).IsEmpty();
        await Assert.That(runner.Ran).IsEmpty();
    }

    [Test]
    [RunOn(OS.Windows)]
    public async Task On_windows_a_kcap_on_path_is_named_as_it_is_on_disk() {
        var shim   = Tmp.CreateFile(@"npm\kcap.cmd", "@echo off\r\n");
        var runner = new Answers("1.3.0");

        var copies = new LocalClientCopies(runner, Tmp.PathTo("npm"), self: null, []).Find();

        await Assert.That(copies.Select(c => c.Path)).IsEquivalentTo([shim]);
    }
}
