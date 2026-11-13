using Capacitor.App.Services;
using Capacitor.App.Services.CliAdoption;
using Capacitor.App.Services.Mutation;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Install;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit.Services.CliAdoption;

/// <summary>The check each renamed entry passes before it is deleted: a tree put in its place since the identity
/// check must fail it.</summary>
public class AdoptionDeletionTests {
    [TempDir] public required TempDir Tmp { get; init; }

    (OtherCliInstall Install, ScriptInstallLayout Layout) Script(string version = "1.2.3") {
        var layout = new ScriptInstallLayout(Tmp.CreateDir("kcap"));
        Tmp.CreateDir("kcap", "versions", version, "bin");
        File.CreateSymbolicLink(layout.Current, layout.VersionDir(version));
        File.WriteAllText(layout.Marker, "{}");
        var bin = Path.Combine(layout.VersionDir(version), "bin");
        return (new OtherCliInstall(OtherCliInstallKind.Script, layout.Root, version, Path.Combine(bin, "kcap"),
            Path.Combine(bin, "kcap-daemon"), "", [layout.Versions, layout.Current, layout.Marker]), layout);
    }

    static string Aside(string entry) {
        var aside = entry + ".retired-0123";
        if (new FileInfo(entry).LinkTarget is { } target) {
            File.CreateSymbolicLink(aside, target);
            File.Delete(entry);
        } else if (Directory.Exists(entry)) Directory.Move(entry, aside);
        else File.Move(entry, aside);
        return aside;
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_script_install_still_at_its_version_passes() {
        var (install, layout) = Script();

        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Versions, Aside(layout.Versions))).IsTrue();
        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Current, Aside(layout.Current))).IsTrue();
        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Marker, Aside(layout.Marker))).IsTrue();
    }

    /// <summary>A reinstall adds its own version folder beside the old one and moves <c>current</c> to it.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_script_install_reinstalled_since_the_check_fails() {
        var (install, layout) = Script();
        Tmp.CreateDir("kcap", "versions", "2.0.0", "bin");
        File.Delete(layout.Current);
        File.CreateSymbolicLink(layout.Current, layout.VersionDir("2.0.0"));

        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Versions, Aside(layout.Versions))).IsFalse();
        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Current, Aside(layout.Current))).IsFalse();
    }

    /// <summary>The daemon is checked in versions, the folder holding it; current, which only resolves into versions,
    /// follows it once versions is gone.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_script_install_nothing_holds_is_retired_whole() {
        var (layout, install) = await ScriptWithBinariesAsync();
        var config = new ConfigRoot(Tmp.CreateDir("config"));
        var store  = new AdoptionRecordStore(config);
        store.Update(r => r with {
            CutoverUtc = DateTime.UtcNow.AddMinutes(-5), Installs = [new RecordedInstall(install, InstallState.Pending, null, null, null, null)],
        });
        var deletion = new AdoptionDeletion(new Machine(install), store, new AsideRetirement(config, "cli-adoption-retire"),
            TestHarnesses.Under(new UserHome(Tmp.CreateDir("home"))), Tmp.CreateDir("kcap.app"), (_, _) => { });
        ProcessFact self = new(Environment.ProcessId, "/Applications/kcap.app/Contents/MacOS/Kcap", null, DateTime.UtcNow);

        await deletion.SettleAsync([install], [self], [], [], CancellationToken.None);

        await Assert.That(store.Load().Installs.Single().State).IsEqualTo(InstallState.Switched);
        await Assert.That(Directory.GetFileSystemEntries(layout.Root)).IsEmpty();
    }

    async Task<(ScriptInstallLayout Layout, OtherCliInstall Install)> ScriptWithBinariesAsync() {
        var (install, layout) = Script();
        File.WriteAllText(install.CliBinary, "kcap");
        File.WriteAllText(install.DaemonBinary, "kcap-daemon");
        var identity = await new EntryIdentity(new ProcessRunner(TimeProvider.System)).OfAsync(install.DaemonBinary, CancellationToken.None);
        return (layout, install with { Identity = identity! });
    }

    /// <summary>Reads the install and each identity as they are; nothing else is asked of it.</summary>
    sealed class Machine(OtherCliInstall install) : IAdoptionMachine {
        readonly EntryIdentity _identity = new(new ProcessRunner(TimeProvider.System));

        public Task<OtherCliInstall?> ReadInstallAsync(string binary, CancellationToken ct) =>
            Task.FromResult<OtherCliInstall?>(File.Exists(binary) ? install : null);

        public Task<string?> IdentityAsync(string entry, CancellationToken ct) => _identity.OfAsync(entry, ct);

        public string? StartToken(int pid) => null;

        public Task<AdoptionEvidence> ReadEvidenceAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<LaunchdJob> ReadJobAsync(string unitName, CancellationToken ct) => throw new NotSupportedException();
        public string? AppDaemonName => throw new NotSupportedException();
        public Task<MutationOutcome> RepointAsync(OtherCliInstall install, Func<CancellationToken, Task<bool>> stillSame, CancellationToken ct) =>
            throw new NotSupportedException();
        public Task ReattachAsync() => throw new NotSupportedException();
        public IReadOnlyList<ProcessFact> ReadProcesses() => throw new NotSupportedException();
        public Task<IReadOnlyList<ReferenceSite>> ReadSitesAsync(CancellationToken ct) => throw new NotSupportedException();
        public bool StillRuns(int pid, string startToken) => throw new NotSupportedException();
        public Task<bool> WaitForExitAsync(int pid, string? startToken, TimeSpan bound, CancellationToken ct) => throw new NotSupportedException();
    }

    /// <summary>The root helper is told every version versions held when the app planned it, and only versions gets a
    /// listing.</summary>
    [Test, ExcludeOn(OS.Windows)]
    public async Task A_privileged_versions_entry_carries_every_version_it_held() {
        var (install, layout) = Script();
        Tmp.CreateDir("kcap", "versions", "1.1.0");
        var retirement = new AsideRetirement(new ConfigRoot(Tmp.CreateDir("config")), "cli-adoption-retire");

        var versions = AdoptionPrivilegedStep.Entry(install, retirement.Plan(layout.Versions));
        var current  = AdoptionPrivilegedStep.Entry(install, retirement.Plan(layout.Current));

        await Assert.That(versions.Children).IsEquivalentTo(["1.1.0", "1.2.3"]);
        await Assert.That(current.Children).IsNull();
    }

    [Test, ExcludeOn(OS.Windows)]
    public async Task A_versions_folder_without_the_recorded_version_fails() {
        var (install, layout) = Script();
        Directory.Delete(layout.VersionDir("1.2.3"), recursive: true);

        await Assert.That(AdoptionDeletion.StillOurs(install, layout.Versions, Aside(layout.Versions))).IsFalse();
    }
}
