using TUnit.Core.Enums;
using Capacitor.App.Services.LocalSetup;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Tests.Unit;

[RunOn(OS.Windows)]
public class StaleClientRepairWindowsTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner(ProcessResult answer) : IProcessRunner {
        public List<(string File, string[] Args, RunOptions Options)> Ran { get; } = [];

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Ran.Add((fileName, args, options));
            return Task.FromResult(answer);
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options, Action<StreamedLine> onLine,
            CancellationToken ct) => throw new NotSupportedException();
    }

    static readonly ProcessResult Ok = new(0, "", "", false);

    sealed class Scripted(Func<IReadOnlyList<ClientCopy>> find) : ILocalClientCopies {
        public IReadOnlyList<ClientCopy> Find() => find();
    }

    /// Reports <paramref name="path"/> below the floor until <paramref name="fixedBy"/> holds.
    static Func<string?, ILocalClientCopies> Stale(string path, Func<bool>? fixedBy = null, ClientCopyKind kind = ClientCopyKind.OnPath) =>
        _ => new Scripted(() => fixedBy?.Invoke() == true ? [] : [new ClientCopy(kind, path, "1.3.0-beta.8")]);

    string AppCli() => Tmp.CreateExecutable(@"AppData\Local\kcap\current\bin\kcap.exe", "");

    static StaleClientRepair Repair(Runner runner, string appCli, Func<string?, ILocalClientCopies> copies, string terminalPath) =>
        new(runner, new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult<string?>(terminalPath) }, appCli, copies);

    /// npm's global prefix holds the kcap.cmd shim beside node_modules, and npm itself sits with node: the npm on PATH
    /// is told which prefix to update.
    [Test]
    public async Task An_older_npm_install_is_updated_through_npm_into_its_own_prefix() {
        var app    = AppCli();
        var prefix = Tmp.CreateDir("npm").Path;
        var shim   = Tmp.CreateFile(@"npm\kcap.cmd", "@echo off\r\n");
        Tmp.CreateFile(@"npm\node_modules\@kurrent\kcap\package.json", "{}");
        var npm    = Tmp.CreateFile(@"nodejs\npm.cmd", "@echo off\r\n");
        var runner = new Runner(Ok);

        var outcome = await Repair(runner, app, Stale(shim, () => runner.Ran.Count > 0), Tmp.PathTo("nodejs")).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Repaired(shim));
        var (file, args, options) = runner.Ran.Single();
        await Assert.That(file.ToUpperInvariant()).IsEqualTo(npm.ToUpperInvariant());
        await Assert.That(args).IsEquivalentTo(["install", "--global", "--prefix", prefix, "@kurrent/kcap@latest"]);
        await Assert.That(options.EnvOverlay!["PATH"].Split(';')[0].ToUpperInvariant()).IsEqualTo(Tmp.PathTo("nodejs").ToUpperInvariant());
    }

    /// npm's newest release can still be one with no local edition for Windows: the update ran, so the user is told that
    /// rather than that setup could not update it.
    [Test]
    public async Task An_npm_install_still_too_old_after_npm_updated_it_says_so() {
        var app    = AppCli();
        var shim   = Tmp.CreateFile(@"npm\kcap.cmd", "@echo off\r\n");
        Tmp.CreateFile(@"npm\node_modules\@kurrent\kcap\package.json", "{}");
        Tmp.CreateFile(@"nodejs\npm.cmd", "@echo off\r\n");
        var runner = new Runner(Ok);
        Func<string?, ILocalClientCopies> stillOld = _ => new Scripted(() => [new ClientCopy(ClientCopyKind.OnPath, shim, "1.4.0")]);

        var outcome = await Repair(runner, app, stillOld, Tmp.PathTo("nodejs")).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.StillOld(shim, "1.4.0"));
        await Assert.That(runner.Ran.Count).IsEqualTo(1);
    }

    /// An npm that cannot update its install leaves a copy only the user can deal with, since Windows has no relink, and
    /// the user is told why in npm's own words.
    [Test]
    public async Task An_npm_install_npm_cannot_update_is_left_for_the_user_with_npms_reason() {
        var app    = AppCli();
        var shim   = Tmp.CreateFile(@"npm\kcap.cmd", "@echo off\r\n");
        Tmp.CreateFile(@"npm\node_modules\@kurrent\kcap\package.json", "{}");
        Tmp.CreateFile(@"nodejs\npm.cmd", "@echo off\r\n");
        var runner = new Runner(new ProcessResult(1, "",
            "npm error code EEXIST\nnpm error path C:\\npm\\kcap.cmd\nnpm error EEXIST: file already exists\nnpm error A complete log of this run can be found in: x.log", false));

        var outcome = await Repair(runner, app, Stale(shim), Tmp.PathTo("nodejs")).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Failed(shim, "npm could not update it (EEXIST: file already exists)."));
        await Assert.That(runner.Ran.Count).IsEqualTo(1);
        await Assert.That(File.ReadAllText(shim)).IsEqualTo("@echo off\r\n");
    }

    [Test]
    public async Task An_older_kcap_npm_does_not_own_is_left_for_the_user_untouched() {
        var app    = AppCli();
        var stale  = Tmp.CreateExecutable(@"tools\kcap.exe", "old");
        Tmp.CreateFile(@"tools\node_modules\@kurrent\kcap\package.json", "{}");
        var runner = new Runner(Ok);

        var outcome = await Repair(runner, app, Stale(stale), Tmp.PathTo("tools")).RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.Unrepairable(stale));
        await Assert.That(runner.Ran).IsEmpty();
        await Assert.That(File.ReadAllText(stale)).IsEqualTo("old");
        await Assert.That(new FileInfo(stale).LinkTarget).IsNull();
    }

    /// The app's own install is brought up to date by updating the app, never by replacing its kcap.exe.
    [Test]
    public async Task An_older_app_install_is_left_for_an_app_update() {
        var installed = Tmp.CreateExecutable(@"home\AppData\Local\kcap\current\bin\kcap.exe", "old");
        var runner    = new Runner(Ok);

        var outcome = await Repair(runner, AppCli(), Stale(installed, kind: ClientCopyKind.DesktopApp), Tmp.PathTo("tools"))
            .RunAsync(CancellationToken.None);

        await Assert.That(outcome).IsEqualTo(new ClientRepairOutcome.OldApp(installed));
        await Assert.That(runner.Ran).IsEmpty();
        await Assert.That(File.ReadAllText(installed)).IsEqualTo("old");
    }
}
