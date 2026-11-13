using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Update;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit.Services.CliAdoption;

public class AdoptionEvidenceReaderTests {
    [TempDir] public required TempDir Tmp { get; init; }

    sealed class Runner : IProcessRunner {
        readonly ProcessRunner _real = new(TimeProvider.System);
        public readonly List<string> Printed = [];
        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            if (fileName != "/bin/launchctl") return _real.RunAsync(fileName, args, options, ct);
            Printed.Add(string.Join(' ', args));
            return Task.FromResult(new ProcessResult(0, "state = running\npid = 321\nprogram = /loaded/kcap-daemon\n", "", false));
        }
        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    readonly Runner _runner = new();
    string Bundle => Tmp.GetResolvedPath("Kcap.app");
    UserHome Home => new(Tmp.PathTo("home"));

    string Exe(string relative) {
        Tmp.CreateExecutable(relative, "#!/bin/sh\n");
        return Tmp.GetResolvedPath(relative);
    }

    string NpmDaemon() {
        Tmp.CreateFile("prefix/lib/node_modules/@kurrent/kcap/bin/kcap.js");
        Tmp.CreateFile("prefix/lib/node_modules/@kurrent/kcap/package.json", """{"version":"1.2.3"}""");
        Exe("prefix/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap");
        return Exe("prefix/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap-daemon");
    }

    void Plist(string name, string program, string? path = null) =>
        Tmp.CreateFile($"home/Library/LaunchAgents/io.kurrent.kcap.daemon.{name}.plist",
            $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>Label</key><string>io.kurrent.kcap.daemon.{name}</string>
              <key>ProgramArguments</key><array><string>{program}</string><string>--name</string><string>{name}</string></array>
              {(path is null ? "" : $"<key>EnvironmentVariables</key><dict><key>PATH</key><string>{path}</string></dict>")}
            </dict></plist>
            """);

    Task<AdoptionEvidence> Read(string? loginPath = "/usr/bin:/bin", string[]? links = null) =>
        new AdoptionEvidenceReader(_runner, new FakeLoginShellProbe { TerminalPathBehavior = _ => Task.FromResult(loginPath) },
            new DaemonStore(Tmp.CreateDir("daemons")), Home, Bundle, links ?? [Tmp.CreateDir("links")]).ReadAsync(CancellationToken.None);

    [Test, RunOn(OS.MacOs)]
    public async Task A_mac_with_no_other_install_runs_no_launchctl() {
        Plist("work", $"{Bundle}/Contents/MacOS/kcap-daemon");
        Exe("Kcap.app/Contents/MacOS/kcap-daemon");

        var evidence = await Read();

        await Assert.That(_runner.Printed.Count).IsEqualTo(0);
        await Assert.That(evidence.Installs.Count).IsEqualTo(0);
        await Assert.That(evidence.Units.Count).IsEqualTo(1);
        await Assert.That(evidence.Units[0].Loaded).IsFalse();
    }

    [Test, RunOn(OS.MacOs)]
    public async Task Units_are_printed_only_once_an_install_is_found() {
        Plist("work", NpmDaemon());

        var evidence = await Read();

        await Assert.That(_runner.Printed.Count).IsEqualTo(1);
        await Assert.That(_runner.Printed[0].EndsWith("/io.kurrent.kcap.daemon.work", StringComparison.Ordinal)).IsTrue();
        await Assert.That(evidence.Installs.Count).IsEqualTo(1);
        await Assert.That(evidence.Units[0].Loaded).IsTrue();
        await Assert.That(evidence.Units[0].JobPid).IsEqualTo(321);
        await Assert.That(evidence.Units[0].LoadedProgram).IsEqualTo("/loaded/kcap-daemon");
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_unit_naming_something_other_than_the_bundle_is_printed_even_when_it_is_no_install() {
        Plist("work", Exe("opt/homebrew/bin/kcap-daemon"));

        var evidence = await Read();

        await Assert.That(_runner.Printed.Count).IsEqualTo(1);
        await Assert.That(evidence.Installs.Count).IsEqualTo(0);
    }

    [Test, RunOn(OS.MacOs)]
    public async Task A_running_daemon_is_read_from_its_pid_file_with_its_executable() {
        var store = new DaemonStore(Tmp.CreateDir("daemons"));
        File.WriteAllText(store.PidPath("work"), $"{Environment.ProcessId}\n{ProcessStartToken.ForCurrent()}\n");

        var evidence = await new AdoptionEvidenceReader(_runner, new FakeLoginShellProbe(), store, Home, Bundle, [Tmp.CreateDir("links")])
            .ReadAsync(CancellationToken.None);

        await Assert.That(evidence.Daemons.Count).IsEqualTo(1);
        await Assert.That(evidence.Daemons[0].Name).IsEqualTo("work");
        await Assert.That(evidence.Daemons[0].Pid).IsEqualTo(Environment.ProcessId);
        await Assert.That(evidence.Daemons[0].StartToken).IsEqualTo(ProcessStartToken.ForCurrent());
        await Assert.That(evidence.Daemons[0].Executable).IsEqualTo(ProcessExecutable.Of(Environment.ProcessId));
    }

    [Test, RunOn(OS.MacOs)]
    public async Task The_login_PATH_and_each_unit_PATH_report_their_first_kcap_unresolved() {
        var real = Exe("real/kcap");
        var login = Tmp.CreateDir("login-bin");
        File.CreateSymbolicLink(Path.Combine(login, "kcap"), real);
        var stored = Exe("stored-bin/kcap");
        Plist("work", $"{Bundle}/Contents/MacOS/kcap-daemon", $"{Tmp.GetResolvedPath("empty")}:{Tmp.GetResolvedPath("stored-bin")}");

        var evidence = await Read(login.Path);

        await Assert.That(evidence.LoginPath).IsEqualTo(login.Path);
        await Assert.That(evidence.KcapOnLoginPath).IsEqualTo(Path.Combine(login, "kcap"));
        await Assert.That(evidence.Units[0].KcapOnStoredPath).IsEqualTo(stored);
    }

    [Test, RunOn(OS.MacOs)]
    public async Task Evidence_from_a_unit_a_PATH_and_a_link_names_one_install_once() {
        Tmp.CreateFile("kcap-home/install.json", "{}");
        Exe("kcap-home/versions/1.0.0/bin/kcap");
        var daemon = Exe("kcap-home/versions/1.0.0/bin/kcap-daemon");
        var root = Tmp.GetResolvedPath("kcap-home");
        File.CreateSymbolicLink($"{root}/current", $"{root}/versions/1.0.0");
        var onPath = Tmp.CreateDir("path-bin");
        File.CreateSymbolicLink(Path.Combine(onPath, "kcap"), $"{root}/current/bin/kcap");
        var links = Tmp.CreateDir("links");
        File.CreateSymbolicLink(Path.Combine(links, "kcap"), $"{root}/versions/1.0.0/bin/kcap");
        Plist("work", $"{root}/current/bin/kcap-daemon");

        var evidence = await Read(onPath.Path, [links.Path]);

        await Assert.That(evidence.Installs.Count).IsEqualTo(1);
        await Assert.That(evidence.Installs[0].DaemonBinary).IsEqualTo(daemon);
        await Assert.That(evidence.Links).IsEquivalentTo([Path.Combine(links.Path, "kcap"), Path.Combine(onPath.Path, "kcap")]);
    }

    /// <summary>A kcap a PATH reaches only after another is a link all the same, which dangles once its install goes.</summary>
    [Test, RunOn(OS.MacOs)]
    public async Task A_kcap_later_on_the_login_PATH_or_a_unit_PATH_is_link_evidence() {
        Exe("first-bin/kcap");
        var later  = Tmp.CreateDir("later-bin");
        var stored = Tmp.CreateDir("stored-bin");
        File.CreateSymbolicLink(Path.Combine(later, "kcap"), Exe("real/kcap"));
        File.CreateSymbolicLink(Path.Combine(stored, "kcap"), Exe("real/kcap"));
        Plist("work", $"{Bundle}/Contents/MacOS/kcap-daemon", $"{Tmp.PathTo("first-bin")}:{stored.Path}");

        var evidence = await Read($"{Tmp.PathTo("first-bin")}:relative:{later.Path}");

        await Assert.That(evidence.Links).Contains(Path.Combine(later.Path, "kcap"));
        await Assert.That(evidence.Links).Contains(Path.Combine(stored.Path, "kcap"));
    }

    /// <summary>An install no fixed folder and no first PATH match reaches is found through a later PATH match, so its
    /// link and the install are switched together.</summary>
    [Test, RunOn(OS.MacOs)]
    public async Task An_install_only_a_later_PATH_match_reaches_is_found() {
        NpmDaemon();
        Exe("first-bin/kcap");
        var later = Tmp.CreateDir("later-bin");
        File.CreateSymbolicLink(Path.Combine(later, "kcap"), Tmp.GetResolvedPath("prefix", "lib", "node_modules", "@kurrent", "kcap", "bin", "kcap.js"));

        var evidence = await Read($"{Tmp.PathTo("first-bin")}:{later.Path}");

        await Assert.That(evidence.Installs.Count).IsEqualTo(1);
        await Assert.That(evidence.Installs[0].Root).IsEqualTo(Tmp.GetResolvedPath("prefix", "lib", "node_modules", "@kurrent", "kcap"));
        await Assert.That(evidence.Links).Contains(Path.Combine(later.Path, "kcap"));
    }

    /// <summary>npm links an install's command into its own prefix, which no PATH may list, as for an nvm version not in
    /// use.</summary>
    [Test, RunOn(OS.MacOs)]
    public async Task An_npm_install_s_own_link_is_link_evidence_on_no_PATH() {
        Plist("work", NpmDaemon());
        File.CreateSymbolicLink(Tmp.CreateDir("prefix", "bin").PathTo("kcap"), "../lib/node_modules/@kurrent/kcap/bin/kcap.js");

        var evidence = await Read();

        await Assert.That(evidence.Installs.Count).IsEqualTo(1);
        await Assert.That(evidence.Links).IsEquivalentTo([Tmp.GetResolvedPath("prefix", "bin", "kcap")]);
    }
}
