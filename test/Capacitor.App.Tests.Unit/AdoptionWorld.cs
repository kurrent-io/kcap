using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// <summary>A Mac with a user-owned npm install of kcap in a temporary tree, a daemon service running it, and the
/// takeover over it. The record, the PATH links, the integrations refresh, the deletion and the notices are the real
/// ones; launchd, the daemon, the process list and the reference sites are scripted on <see cref="Machine"/>. Each
/// <see cref="Launch"/> is a new app run over the same files.</summary>
sealed class AdoptionWorld {
    public const string DaemonName = "mac";
    public const int OldDaemonPid = 4100;
    public const int NewDaemonPid = 4200;
    public const string Version = "1.4.1-beta.2";

    public static readonly TimeSpan Bounded = TimeSpan.FromSeconds(15);

    readonly Lock _lock = new();
    readonly List<(string Event, Dictionary<string, string?> Fields)> _log = [];

    public AdoptionWorld(TempDir tmp) {
        tmp.CreateDir("home", ".local", "bin");
        tmp.CreateDir("home", "Library", "LaunchAgents");
        Home    = tmp.GetResolvedPath("home");
        Prefix  = tmp.GetResolvedPath("brew");
        Stable  = [Path.Combine(Home, ".local", "bin")];
        Config  = new ConfigRoot(tmp.CreateDir("config"));

        tmp.CreateFile("brew/lib/node_modules/@kurrent/kcap/package.json", $$"""{"name":"@kurrent/kcap","version":"{{Version}}"}""");
        tmp.CreateExecutable("brew/lib/node_modules/@kurrent/kcap/bin/kcap.js", "#!/usr/bin/env node\n");
        tmp.CreateFile("brew/lib/node_modules/@kurrent/kcap-darwin-arm64/package.json", """{"name":"@kurrent/kcap-darwin-arm64"}""");
        tmp.CreateExecutable("brew/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap", "#!/bin/sh\nexit 0\n");
        tmp.CreateExecutable("brew/lib/node_modules/@kurrent/kcap-darwin-arm64/bin/kcap-daemon", "#!/bin/sh\nexit 0\n");
        Root     = tmp.GetResolvedPath("brew", "lib", "node_modules", "@kurrent", "kcap");
        Platform = tmp.GetResolvedPath("brew", "lib", "node_modules", "@kurrent", "kcap-darwin-arm64");
        Install  = new OtherCliInstall(OtherCliInstallKind.Npm, Root, Version, Path.Combine(Platform, "bin", "kcap"),
            Path.Combine(Platform, "bin", "kcap-daemon"), "16777232:4242:1:1", [Root, Platform]);

        Link = Path.Combine(tmp.CreateDir("brew", "bin"), "kcap");
        File.CreateSymbolicLink(Link, "../lib/node_modules/@kurrent/kcap/bin/kcap.js");

        tmp.CreateExecutable("Applications/kcap.app/Contents/MacOS/kcap", "#!/bin/sh\nexit 0\n");
        tmp.CreateExecutable("Applications/kcap.app/Contents/MacOS/kcap-daemon", "#!/bin/sh\nexit 0\n");
        var bundleRoot = tmp.GetResolvedPath("Applications", "kcap.app");
        Bundle = new AdoptionBundle(bundleRoot, Path.Combine(bundleRoot, "Contents", "MacOS", "kcap"), "2.0.0", InSystemApplications: true);

        Plist = Path.Combine(Home, "Library", "LaunchAgents", $"io.kurrent.kcap.daemon.{DaemonName}.plist");
        WritePlist(Install.DaemonBinary);

        Probe     = new FakeLoginShellProbe { KcapPathFreshBehavior = _ => Task.FromResult<string?>(Bundle.Cli) };
        Shim      = new StableShim(Path.Combine(Stable[0], "kcap"));
        Harnesses = TestHarnesses.Under(new UserHome(Home));
        Machine   = new ScriptedMachine(this);
        Window    = new BehaviorSubject<IAppNotifier?>(Notifier);
    }

    public string Home { get; }
    public string Prefix { get; }
    public string Root { get; }
    public string Platform { get; }
    public string Link { get; }
    public string Plist { get; }
    public IReadOnlyList<string> Stable { get; }
    public ConfigRoot Config { get; }
    public OtherCliInstall Install { get; }
    public AdoptionBundle Bundle { get; }
    public string LoginPath => $"{Prefix}/bin:/usr/bin:/bin";

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    public FakeLoginShellProbe Probe { get; }
    public StableShim Shim { get; }
    public RefreshRunner Refresh { get; } = new();
    public HarnessRegistry Harnesses { get; }
    public ScriptedMachine Machine { get; }
    public MemoryAppState AppState { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public BehaviorSubject<IAppNotifier?> Window { get; }
    public Func<string, bool> UserCanWrite { get; set; } = _ => true;
    public AdoptionNotices? Notices { get; private set; }

    public AdoptionRecord Record => new AdoptionRecordStore(Config).Load();

    public bool RecordWritten => File.Exists(Config.Path(AdoptionRecordStore.FileName));

    public IReadOnlyList<string> Events {
        get { lock (_lock) return [.. _log.Select(e => e.Event)]; }
    }

    public IReadOnlyList<Dictionary<string, string?>> Logged(string evt) {
        lock (_lock) return [.. _log.Where(e => e.Event == evt).Select(e => e.Fields)];
    }

    /// <summary>What the evidence reader finds while the npm install runs the service and owns the PATH link.</summary>
    public AdoptionEvidence Found() =>
        new([Install], [new ServiceUnitFact(Plist, DaemonName, Install.DaemonBinary, LoginPath, Link, true, OldDaemonPid, Install.DaemonBinary)],
            [new RunningDaemonFact(DaemonName, OldDaemonPid, $"tok-{OldDaemonPid}", Install.DaemonBinary)], LoginPath, Link);

    /// <summary>What it finds once the service and the link name the bundle: no other install.</summary>
    public AdoptionEvidence AfterSwitch() =>
        new([], [new ServiceUnitFact(Plist, DaemonName, Bundle.Daemon, LoginPath, Link, true, NewDaemonPid, Bundle.Daemon)],
            [new RunningDaemonFact(DaemonName, NewDaemonPid, $"tok-{NewDaemonPid}", Bundle.Daemon)], LoginPath, Link);

    /// <summary>A harness session that started an hour before any cutover this world records.</summary>
    public ProcessFact OldSession(int pid) => new(pid, "/opt/homebrew/bin/codex", null, Time.GetUtcNow().UtcDateTime.AddHours(-1));

    public CliAdoptionCoordinator Launch(IAppStateStore? appState = null, CancellationToken lifetime = default) {
        var store = new AdoptionRecordStore(Config);
        Notices = new AdoptionNotices(Window, store);
        var batch = new AdoptionBatch(Machine, store, new AsideRetirement(Config, "cli-adoption-retire"),
            new AdoptionIntegrations(new ApplicationIntegrationRefresh(Config, Refresh), Bundle.Cli, store, Time),
            new AdoptionPathStep(Probe, new AdoptedLinkInventory(Config), store, Shim, Stable), Notices, appState ?? AppState,
            Harnesses, Bundle, Stable, folder => UserCanWrite(folder), Write);
        return new CliAdoptionCoordinator(batch, Time, Write, lifetime);
    }

    /// <summary>Starts <paramref name="launch"/> and waits for its first look and the notices it posted.</summary>
    public async Task FirstLookAsync(CliAdoptionCoordinator launch) {
        launch.Start();
        await launch.FirstLook.WaitAsync(Bounded);
        await SettledAsync(launch);
    }

    /// <summary>Fires the recheck timer, when there is one, and waits for what it ran.</summary>
    public async Task RecheckAsync(CliAdoptionCoordinator launch) {
        Time.Advance(CliAdoptionCoordinator.RecheckInterval);
        await SettledAsync(launch);
    }

    async Task SettledAsync(CliAdoptionCoordinator launch) {
        await launch.Settled.WaitAsync(Bounded);
        if (Notices is { } notices) await notices.Settled.WaitAsync(Bounded);
    }

    public string? PlistProgram() => LaunchdPrint.Binary(File.ReadAllText(Plist));

    public void WritePlist(string program) => File.WriteAllText(Plist, $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.{DaemonName}</string>
          <key>ProgramArguments</key><array><string>{program}</string><string>run</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """);

    void Write(string evt, (string Key, string? Value)[] fields) {
        lock (_lock) _log.Add((evt, fields.ToDictionary(f => f.Key, f => f.Value)));
    }

    /// <summary>launchd and the daemon as the repoint CLI leaves them: by default the move rewrites the plist and
    /// launchd runs the bundle's daemon under a new pid.</summary>
    internal sealed class ScriptedMachine(AdoptionWorld world) : IAdoptionMachine {
        public Func<CancellationToken, Task<AdoptionEvidence>> Evidence { get; set; } = _ => Task.FromResult(world.Found());
        public Func<OtherCliInstall, MutationOutcome>? Repoint { get; set; }
        public Dictionary<string, LaunchdJob> Jobs { get; } = new() { [DaemonName] = new(true, OldDaemonPid, world.Install.DaemonBinary) };
        public List<ProcessFact> Processes { get; set; } = [];
        public List<ReferenceSite> Sites { get; set; } = [];
        public HashSet<int> Alive { get; } = [];
        public int EvidenceReads { get; private set; }
        public int ProcessReads { get; private set; }
        public int SiteReads { get; private set; }
        public int Repoints { get; private set; }
        public int Reattaches { get; private set; }

        public string? AppDaemonName { get; set; } = DaemonName;

        public Task<AdoptionEvidence> ReadEvidenceAsync(CancellationToken ct) {
            EvidenceReads++;
            return Evidence(ct);
        }

        public Task<OtherCliInstall?> ReadInstallAsync(string binary, CancellationToken ct) =>
            Task.FromResult(binary == world.Install.CliBinary && File.Exists(binary) ? world.Install : null);

        public Task<LaunchdJob> ReadJobAsync(string unitName, CancellationToken ct) =>
            Task.FromResult(Jobs.TryGetValue(unitName, out var job) ? job : new LaunchdJob(false, null, null));

        public async Task<MutationOutcome> RepointAsync(
                OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, CancellationToken ct) {
            Repoints++;
            if (!await stillSame(ct)) return new MutationOutcome.Refused(AdoptionRepoint.InstallChanged, RecoverySurface.None);
            if (Repoint is { } scripted) return scripted(install);

            world.WritePlist(world.Bundle.Daemon);
            Jobs[DaemonName] = new LaunchdJob(true, NewDaemonPid, world.Bundle.Daemon);
            return new MutationOutcome.Succeeded();
        }

        public Task ReattachAsync() {
            Reattaches++;
            return Task.CompletedTask;
        }

        public IReadOnlyList<ProcessFact> ReadProcesses() {
            ProcessReads++;
            return [.. Processes];
        }

        /// <summary><see cref="Sites"/> after the service file and launchd's job, read as they are now.</summary>
        public Task<IReadOnlyList<ReferenceSite>> ReadSitesAsync(CancellationToken ct) {
            SiteReads++;
            var job = Jobs.GetValueOrDefault(DaemonName);
            return Task.FromResult<IReadOnlyList<ReferenceSite>>([
                new(world.Plist, File.ReadAllText(world.Plist), true),
                new($"launchd:io.kurrent.kcap.daemon.{DaemonName}", job is { Loaded: true } ? job.Program : null, true),
                .. Sites,
            ]);
        }

        public string? StartToken(int pid) => $"tok-{pid}";

        public bool StillRuns(int pid, string startToken) => Alive.Contains(pid) && startToken == $"tok-{pid}";
    }

    /// <summary>The bundle's <c>kcap refresh --app-migration</c>, which succeeds unless told otherwise.</summary>
    internal sealed class RefreshRunner : IProcessRunner {
        int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public int ExitCode { get; set; }

        public Task<ProcessResult> RunAsync(string fileName, string[] args, RunOptions options, CancellationToken ct) {
            Interlocked.Increment(ref _calls);
            return Task.FromResult(new ProcessResult(ExitCode, "", ExitCode == 0 ? "" : "refresh failed", false));
        }

        public Task<StreamingResult> RunStreamingAsync(string fileName, string[] args, RunOptions options,
            Action<StreamedLine> onLine, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The stable <c>~/.local/bin/kcap</c> link the PATH step asks for.</summary>
    internal sealed class StableShim(string link) : ICliPathInstaller {
        public string Disclosure => "";

        public ShimPreflight Preflight(string target) => ShimPreflight.Installable;

        public Task<ShimResult> InstallAsync(string target, CancellationToken ct) {
            if (new FileInfo(link).LinkTarget is null) File.CreateSymbolicLink(link, target);
            return Task.FromResult(new ShimResult(ShimOutcome.Installed, null));
        }
    }

    internal sealed class MemoryAppState : IAppStateStore {
        readonly Lock _lock = new();
        AppState _state = new();

        public AppState State {
            get { lock (_lock) return _state; }
        }

        public Task<AppState> LoadAsync() => Task.FromResult(State);

        public Task<bool> UpdateAsync(Func<AppState, AppState> mutate) {
            lock (_lock) _state = mutate(_state);
            return Task.FromResult(true);
        }
    }
}
