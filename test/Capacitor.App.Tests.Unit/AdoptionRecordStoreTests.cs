using System.Runtime.Versioning;
using Capacitor.App.Services.CliAdoption;
using Capacitor.Cli.Core;
using TUnit.Core.Enums;

namespace Capacitor.App.Tests.Unit;

public class AdoptionRecordStoreTests {
    [TempDir] public required TempDir Tmp { get; init; }

    ConfigRoot Config => new(Tmp.PathTo("config"));
    string FilePath => Config.Path("cli-adoption.json");

    static OtherCliInstall Install(string root) => new(
        OtherCliInstallKind.Npm, root, "1.2.3", root + "/bin/kcap", root + "/bin/kcap-daemon", "ident",
        [root + "/a", root + "/b"]);

    [Test]
    public async Task A_record_round_trips_every_field() {
        var record = new AdoptionRecord(
            new DateTime(2026, 11, 4, 10, 30, 0, DateTimeKind.Utc),
            [
                new RecordedInstall(Install("/p1"), InstallState.Left, LeftReason.PromptCancelled, "me", 42, "tok"),
                new RecordedInstall(Install("/p2"), InstallState.Pending, null, null, null, null)
            ],
            [new LinkChange("/usr/local/bin/kcap", "/old", "/new")],
            new PrivilegedOperation("/p1", 7, "start", true),
            ["notice-a", "notice-b"],
            true);

        new AdoptionRecordStore(Config).Update(_ => record);
        var loaded = new AdoptionRecordStore(Config).Load();

        await Assert.That(loaded.CutoverUtc).IsEqualTo(record.CutoverUtc);
        await Assert.That(loaded.Installs.Count).IsEqualTo(2);
        await Assert.That(loaded.Installs[0].Install.Kind).IsEqualTo(OtherCliInstallKind.Npm);
        await Assert.That(loaded.Installs[0].Install.Identity).IsEqualTo("ident");
        await Assert.That(loaded.Installs[0].Install.OwnedEntries.SequenceEqual(["/p1/a", "/p1/b"])).IsTrue();
        await Assert.That(loaded.Installs[0].State).IsEqualTo(InstallState.Left);
        await Assert.That(loaded.Installs[0].Reason).IsEqualTo(LeftReason.PromptCancelled);
        await Assert.That(loaded.Installs[0].HeldBy).IsEqualTo("me");
        await Assert.That(loaded.Installs[0].HolderPid).IsEqualTo(42);
        await Assert.That(loaded.Installs[0].HolderStartToken).IsEqualTo("tok");
        await Assert.That(loaded.Installs[1].Reason).IsNull();
        await Assert.That(loaded.Links.Single()).IsEqualTo(record.Links[0]);
        await Assert.That(loaded.Privileged).IsEqualTo(record.Privileged);
        await Assert.That(loaded.NoticesShown.SequenceEqual(["notice-a", "notice-b"])).IsTrue();
        await Assert.That(loaded.MachineSwitched).IsTrue();
    }

    [Test]
    public async Task A_missing_or_malformed_file_loads_empty_without_deleting_it() {
        var store = new AdoptionRecordStore(Config);
        await Assert.That(store.Load().Installs).IsEmpty();

        Tmp.CreateFile("config/cli-adoption.json", "{ not json");
        var loaded = store.Load();

        await Assert.That(loaded.Installs).IsEmpty();
        await Assert.That(loaded.MachineSwitched).IsFalse();
        await Assert.That(File.ReadAllText(FilePath)).IsEqualTo("{ not json");

        store.Update(r => r with { NoticesShown = ["x"] });
        await Assert.That(store.Load().NoticesShown.Single()).IsEqualTo("x");
    }

    [Test]
    public async Task Pending_is_true_only_while_an_install_waits_and_the_machine_is_not_switched() {
        var waiting = new RecordedInstall(Install("/p"), InstallState.Pending, null, null, null, null);
        var record = AdoptionRecord.Empty with { Installs = [waiting] };

        await Assert.That(AdoptionRecord.Empty.Pending).IsFalse();
        await Assert.That(record.Pending).IsTrue();
        await Assert.That((record with { MachineSwitched = true }).Pending).IsFalse();
        await Assert.That((record with { Installs = [waiting with { State = InstallState.Left }] }).Pending).IsFalse();
    }

    [Test, ExcludeOn(OS.Windows)]
    [UnsupportedOSPlatform("windows")]
    public async Task The_file_is_owner_only() {
        new AdoptionRecordStore(Config).Update(r => r with { MachineSwitched = true });

        await Assert.That(File.GetUnixFileMode(FilePath)).IsEqualTo(UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    [Test]
    public async Task Concurrent_updates_both_land() {
        var store = new AdoptionRecordStore(Config);
        await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(() =>
            store.Update(r => r with { NoticesShown = [.. r.NoticesShown, "n" + i] }))));

        await Assert.That(store.Load().NoticesShown.Count).IsEqualTo(8);
    }

    /// <summary>A file that leaves a list out, as a sparse or older one does, loads that list empty rather than null.</summary>
    [Test]
    public async Task A_sparse_file_loads_every_list_empty() {
        Directory.CreateDirectory(Config.Directory);
        File.WriteAllText(FilePath, "{}");

        var loaded = new AdoptionRecordStore(Config).Load();

        await Assert.That(loaded.Installs).IsEmpty();
        await Assert.That(loaded.Links).IsEmpty();
        await Assert.That(loaded.NoticesShown).IsEmpty();
        await Assert.That(loaded.Pending).IsFalse();

        var updated = new AdoptionRecordStore(Config).Update(r => r with { NoticesShown = [.. r.NoticesShown, "switched:/p"] });
        await Assert.That(updated.NoticesShown).IsEquivalentTo(["switched:/p"]);
    }

    [Test]
    public async Task A_record_without_notices_or_owned_entries_keeps_its_installs() {
        Directory.CreateDirectory(Config.Directory);
        File.WriteAllText(FilePath, """
            {"installs":[{"install":{"kind":"Npm","root":"/p","version":"1.2.3","cli_binary":"/p/bin/kcap",
              "daemon_binary":"/p/bin/kcap-daemon","identity":"ident"},"state":"Pending"}],"links":[]}
            """);

        var loaded = new AdoptionRecordStore(Config).Load();

        await Assert.That(loaded.Installs.Single().Install.Root).IsEqualTo("/p");
        await Assert.That(loaded.Installs.Single().Install.OwnedEntries).IsEmpty();
        await Assert.That(loaded.NoticesShown).IsEmpty();
        await Assert.That(loaded.Pending).IsTrue();
    }
}

