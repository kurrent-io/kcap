using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness;
using Capacitor.Cli.Core.Setup;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.App.Tests.Unit;

/// <summary>A Mac with user-owned npm installs of kcap in a temporary tree, each with a daemon service running it, and
/// the takeover over them. The record, the PATH links, the integrations refresh, the deletion and the notices are the
/// real ones; launchd, the daemon, the process list and the reference sites are scripted on <see cref="Machine"/>, and
/// the evidence is read from the service files and launchd's jobs as they are at that moment. Each
/// <see cref="Launch"/> is a new app run over the same files.</summary>
sealed class AdoptionWorld {
    public const string DaemonName = "mac";
    public const int OldDaemonPid = 4100;
    public const int NewDaemonPid = 4200;
    public const string Version = "1.4.1-beta.2";

    public static readonly TimeSpan Bounded = TimeSpan.FromSeconds(15);

    readonly TempDir _tmp;
    readonly Lock _lock = new();
    readonly List<(string Event, Dictionary<string, string?> Fields)> _log = [];
    readonly List<NpmInstall> _installs = [];

    /// <summary>One npm install: the launcher, the platform package beside it, its <c>bin/kcap</c> link and the
    /// service that runs its daemon.</summary>
    public sealed record NpmInstall(OtherCliInstall Install, string Platform, string Link, string Plist, string Daemon, string StoredPath, int OldPid) {
        public string Root => Install.Root;
        public int NewPid => OldPid + 100;
    }

    public AdoptionWorld(TempDir tmp) {
        _tmp = tmp;
        tmp.CreateDir("home", ".local", "bin");
        tmp.CreateDir("home", "Library", "LaunchAgents");
        Home    = tmp.GetResolvedPath("home");
        Stable  = [Path.Combine(Home, ".local", "bin")];
        Config  = new ConfigRoot(tmp.CreateDir("config"));

        tmp.CreateExecutable("Applications/kcap.app/Contents/MacOS/kcap", "#!/bin/sh\nexit 0\n");
        tmp.CreateExecutable("Applications/kcap.app/Contents/MacOS/kcap-daemon", "#!/bin/sh\nexit 0\n");
        var bundleRoot = tmp.GetResolvedPath("Applications", "kcap.app");
        Bundle = new AdoptionBundle(bundleRoot, Path.Combine(bundleRoot, "Contents", "MacOS", "kcap"), "2.0.0", InSystemApplications: true);

        Probe     = new FakeLoginShellProbe { KcapPathFreshBehavior = _ => Task.FromResult<string?>(Bundle.Cli) };
        Shim      = new StableShim(Path.Combine(Stable[0], "kcap"));
        Harnesses = TestHarnesses.Under(new UserHome(Home));
        Machine   = new ScriptedMachine(this);
        Window    = new BehaviorSubject<IAppNotifier?>(Notifier);

        First = AddInstall("brew", DaemonName, OldDaemonPid);
    }

    public string Home { get; }
    public IReadOnlyList<string> Stable { get; }
    public ConfigRoot Config { get; }
    public AdoptionBundle Bundle { get; }
    public NpmInstall First { get; }
    public IReadOnlyList<NpmInstall> Installs => _installs;

    public string Prefix => Path.GetDirectoryName(Path.GetDirectoryName(First.Link))!;
    public string Root => First.Root;
    public string Platform => First.Platform;
    public string Link => First.Link;
    public string Plist => First.Plist;
    public OtherCliInstall Install => First.Install;
    public string LoginPath => First.StoredPath;

    public FakeTimeProvider Time { get; } = new(new DateTimeOffset(2026, 11, 5, 9, 0, 0, TimeSpan.Zero));
    public FakeLoginShellProbe Probe { get; }
    public StableShim Shim { get; }
    public RefreshRunner Refresh { get; } = new();
    public HarnessRegistry Harnesses { get; }
    public ScriptedMachine Machine { get; }
    public MemoryAppState AppState { get; } = new();
    public RecordingNotifier Notifier { get; } = new();
    public BehaviorSubject<IAppNotifier?> Window { get; }
    public BehaviorSubject<DaemonActivity?> Activity { get; } = new(null);
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

    /// <summary>Lays out an npm install under <paramref name="prefix"/>, its link, and a running service for it.</summary>
    public NpmInstall AddInstall(string prefix, string daemon, int pid) {
        var scope = $"{prefix}/lib/node_modules/@kurrent";
        _tmp.CreateFile($"{scope}/kcap/package.json", $$"""{"name":"@kurrent/kcap","version":"{{Version}}"}""");
        _tmp.CreateExecutable($"{scope}/kcap/bin/kcap.js", "#!/usr/bin/env node\n");
        _tmp.CreateFile($"{scope}/kcap-darwin-arm64/package.json", $$"""{"name":"@kurrent/kcap-darwin-arm64","version":"{{Version}}"}""");
        _tmp.CreateExecutable($"{scope}/kcap-darwin-arm64/bin/kcap", "#!/bin/sh\nexit 0\n");
        _tmp.CreateExecutable($"{scope}/kcap-darwin-arm64/bin/kcap-daemon", "#!/bin/sh\nexit 0\n");
        var root     = _tmp.GetResolvedPath(prefix, "lib", "node_modules", "@kurrent", "kcap");
        var platform = _tmp.GetResolvedPath(prefix, "lib", "node_modules", "@kurrent", "kcap-darwin-arm64");
        var install  = new OtherCliInstall(OtherCliInstallKind.Npm, root, Version, Path.Combine(platform, "bin", "kcap"),
            Path.Combine(platform, "bin", "kcap-daemon"), $"16777232:{pid}:1:1", [root, platform]);

        var link = Path.Combine(_tmp.CreateDir(prefix, "bin"), "kcap");
        File.CreateSymbolicLink(link, "../lib/node_modules/@kurrent/kcap/bin/kcap.js");
        var plist = Path.Combine(Home, "Library", "LaunchAgents", $"io.kurrent.kcap.daemon.{daemon}.plist");

        var added = new NpmInstall(install, platform, link, plist, daemon, $"{Path.GetDirectoryName(link)}:/usr/bin:/bin", pid);
        _installs.Add(added);
        WritePlist(added, install.DaemonBinary);
        Machine.Jobs[daemon] = new LaunchdJob(true, pid, install.DaemonBinary);
        return added;
    }

    /// <summary>What the evidence reader would find now: each service file's program and launchd's job for it, the
    /// daemons those jobs run, and every install one of them or a <c>bin/kcap</c> link still resolves into.</summary>
    public AdoptionEvidence Found() {
        var units = _installs.Select(n => {
            var job = Machine.Jobs.GetValueOrDefault(n.Daemon) ?? new LaunchdJob(false, null, null);
            return new ServiceUnitFact(n.Plist, n.Daemon, ProgramOf(n) ?? "", n.StoredPath, n.Link, job.Loaded, job.Pid, job.Loaded ? job.Program : null);
        }).ToList();
        List<RunningDaemonFact> daemons = [
            .. units.Where(u => u.JobPid is not null).Select(u => new RunningDaemonFact(u.Name, u.JobPid!.Value, $"tok-{u.JobPid}", u.LoadedProgram)),
            .. Machine.OtherDaemons,
        ];
        var named = units.SelectMany(u => new[] { u.Program, u.LoadedProgram }).Concat(daemons.Select(d => d.Executable))
            .Concat(_installs.Select(n => n.Link)).OfType<string>().ToList();
        var installs = _installs
            .Where(n => Directory.Exists(n.Platform) && named.Exists(c => n.Install.OwnedEntries.Any(e => CanonicalPath.IsWithin(c, e))))
            .Select(n => n.Install).ToList();

        return new AdoptionEvidence(installs, units, daemons, LoginPath, Link);
    }

    /// <summary>A harness session that started an hour before any cutover this world records.</summary>
    public ProcessFact OldSession(int pid) => new(pid, "/opt/homebrew/bin/codex", null, Time.GetUtcNow().UtcDateTime.AddHours(-1));

    public CliAdoptionCoordinator Launch(IAppStateStore? appState = null, CancellationToken lifetime = default) {
        var store = new AdoptionRecordStore(Config);
        Notices = new AdoptionNotices(Window, store);
        var batch = new AdoptionBatch(Machine, store, new AsideRetirement(Config, "cli-adoption-retire"),
            new AdoptionIntegrations(new ApplicationIntegrationRefresh(Config, Refresh), Bundle.Cli, store, Time),
            new AdoptionPathStep(Probe, new AdoptedLinkInventory(Config), store, Shim, Stable), Notices, appState ?? AppState,
            Harnesses, Bundle, Stable, folder => UserCanWrite(folder), Write);
        var launch = new CliAdoptionCoordinator(batch, Time, Write, lifetime);
        launch.FollowDaemon(Activity);
        return launch;
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

    /// <summary>Reports the daemon idle, after a moment of being unattached, and waits for what that ran.</summary>
    public async Task IdleAsync(CliAdoptionCoordinator launch) {
        Activity.OnNext(null);
        Activity.OnNext(new DaemonActivity(0, Busy: false));
        await SettledAsync(launch);
    }

    // A run can start another as it ends, so the last one is waited for until none follows.
    public async Task SettledAsync(CliAdoptionCoordinator launch) {
        Task settled;
        do {
            settled = launch.Settled;
            await settled.WaitAsync(Bounded);
        } while (!ReferenceEquals(settled, launch.Settled));
        if (Notices is { } notices) await notices.Settled.WaitAsync(Bounded);
    }

    public string? PlistProgram() => ProgramOf(First);

    static string? ProgramOf(NpmInstall install) => LaunchdPrint.Binary(File.ReadAllText(install.Plist));

    public void WritePlist(string program) => WritePlist(First, program);

    public static void WritePlist(NpmInstall install, string program) => File.WriteAllText(install.Plist, $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <plist version="1.0">
        <dict>
          <key>Label</key><string>io.kurrent.kcap.daemon.{install.Daemon}</string>
          <key>ProgramArguments</key><array><string>{program}</string><string>run</string></array>
          <key>RunAtLoad</key><true/>
        </dict>
        </plist>
        """);

    void Write(string evt, (string Key, string? Value)[] fields) {
        lock (_lock) _log.Add((evt, fields.ToDictionary(f => f.Key, f => f.Value)));
    }

    /// <summary>launchd and the daemon as the repoint CLI leaves them: by default a move rewrites the install's plist
    /// and launchd runs the bundle's daemon under a new pid. <see cref="Repoint"/> returning null takes that default.</summary>
    internal sealed class ScriptedMachine(AdoptionWorld world) : IAdoptionMachine {
        public Func<CancellationToken, Task<AdoptionEvidence>> Evidence { get; set; } = _ => Task.FromResult(world.Found());
        public Func<OtherCliInstall, MutationOutcome?>? Repoint { get; set; }
        public Dictionary<string, LaunchdJob> Jobs { get; } = [];
        public List<RunningDaemonFact> OtherDaemons { get; } = [];
        public List<ProcessFact> Processes { get; set; } = [];
        public List<ReferenceSite> Sites { get; set; } = [];
        public HashSet<int> Alive { get; } = [];

        /// <summary>The process list leaves out the app's own process, as an unreadable one does.</summary>
        public bool ProcessesUnreadable { get; set; }

        /// <summary>Every install reads as unreadable, as one mid <c>npm install -g</c> does.</summary>
        public bool InstallsUnreadable { get; set; }

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
            Task.FromResult(InstallsUnreadable || !File.Exists(binary) ? null : world._installs.Find(n => n.Install.CliBinary == binary)?.Install);

        public Task<LaunchdJob> ReadJobAsync(string unitName, CancellationToken ct) =>
            Task.FromResult(Jobs.TryGetValue(unitName, out var job) ? job : new LaunchdJob(false, null, null));

        public async Task<MutationOutcome> RepointAsync(
                OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, CancellationToken ct) {
            Repoints++;
            if (!await stillSame(ct)) return new MutationOutcome.Refused(AdoptionRepoint.InstallChanged, RecoverySurface.None);
            if (Repoint?.Invoke(install) is { } scripted) return scripted;

            var moved = world._installs.Single(n => n.Install == install);
            WritePlist(moved, world.Bundle.Daemon);
            Jobs[moved.Daemon] = new LaunchdJob(true, moved.NewPid, world.Bundle.Daemon);
            return new MutationOutcome.Succeeded();
        }

        public Task ReattachAsync() {
            Reattaches++;
            return Task.CompletedTask;
        }

        /// <summary><see cref="Processes"/> after the app's own process.</summary>
        public IReadOnlyList<ProcessFact> ReadProcesses() {
            ProcessReads++;
            var self = new ProcessFact(Environment.ProcessId, Path.Combine(world.Bundle.Root, "Contents", "MacOS", "Kcap"), null,
                world.Time.GetUtcNow().UtcDateTime.AddDays(-1));
            return ProcessesUnreadable ? [.. Processes] : [self, .. Processes];
        }

        /// <summary><see cref="Sites"/> after each service file and launchd's job for it, read as they are now.</summary>
        public Task<IReadOnlyList<ReferenceSite>> ReadSitesAsync(CancellationToken ct) {
            SiteReads++;
            List<ReferenceSite> sites = [];
            foreach (var n in world._installs) {
                var job = Jobs.GetValueOrDefault(n.Daemon);
                sites.Add(new ReferenceSite(n.Plist, File.ReadAllText(n.Plist), true));
                sites.Add(new ReferenceSite($"launchd:io.kurrent.kcap.daemon.{n.Daemon}", job is { Loaded: true } ? job.Program : null, true));
            }
            return Task.FromResult<IReadOnlyList<ReferenceSite>>([.. sites, .. Sites]);
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
