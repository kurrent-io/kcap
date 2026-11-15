using Capacitor.App.Platform.Windows;
using TUnit.Assertions.Enums;
using static Capacitor.App.Platform.Windows.LegacyRetirementStep;

namespace Capacitor.App.Tests.Unit.Platform.Windows;

/// What runs once a step fails: the legacy root goes only when nothing runs from it any more, and the entries that
/// uninstall it go only once its updater is gone.
public class LegacyInstallRetirementTests {
    static readonly string Root     = Path.GetFullPath("/local/KurrentCapacitor");
    static readonly string Own      = Path.GetFullPath("/local/kcap");
    static readonly string Shortcut = Path.GetFullPath("/desktop/Kurrent Capacitor.lnk");

    static readonly RunningImage Updater = new(11, Path.Combine(Root, VelopackRoot.UpdateExe), new DateTime(2026, 1, 1));

    static readonly IReadOnlyList<LegacyRetirementStep> Steps = [
        new StopProcesses([Updater]),
        new RepointDaemons(Root),
        new RemoveShortcuts([Shortcut]),
        new RemoveAutostart(["KurrentCapacitor"]),
        new RepointUninstallEntry(Root, Own),
        new MoveUserPath(Path.Combine(Root, "current", "bin", "kcap.exe"), Path.Combine(Own, "current", "bin", "kcap.exe")),
        new RemoveRoot(Root),
        new RemoveUninstallEntries(Root, ["KurrentCapacitor"]),
    ];

    readonly FakeEffects _effects = new();
    readonly List<string> _log = [];

    Task<LegacyRetirementOutcome> Run() =>
        LegacyInstallRetirement.RunAsync(Steps, _effects, (evt, _) => _log.Add(evt), CancellationToken.None);

    [Test]
    public async Task Every_step_runs_in_order_and_is_logged() {
        var outcome = await Run();

        await Assert.That(_effects.Calls).IsEquivalentTo(
            ["stop 11", "repoint", "shortcut", "autostart", "entry", "path", "root", "delete KurrentCapacitor"], CollectionOrdering.Matching);
        await Assert.That(outcome.KeptBecause).IsNull();
        await Assert.That(outcome.Failures).IsEmpty();
        await Assert.That(_log).IsEquivalentTo([
            "legacy_process_stopped", "legacy_daemon_repointed", "legacy_shortcut_removed", "legacy_autostart_removed",
            "legacy_uninstall_entry_repointed", "legacy_path_moved", "legacy_root_removed", "legacy_uninstall_entry_removed",
            "legacy_retired",
        ], CollectionOrdering.Matching);
    }

    /// <summary>A busy daemon still runs from the root, so the root and the entries that uninstall it stay for the
    /// next launch; what does not touch the daemon still moves to this install.</summary>
    [Test]
    public async Task A_daemon_still_on_the_legacy_root_keeps_the_root() {
        _effects.RepointFailure = "Daemon 'laptop' is busy";

        var outcome = await Run();

        await Assert.That(_effects.Calls).IsEquivalentTo(
            ["stop 11", "repoint", "shortcut", "autostart", "entry", "path"], CollectionOrdering.Matching);
        await Assert.That(outcome.KeptBecause).IsEqualTo("the daemon still runs from its folder");
        await Assert.That(outcome.Message).Contains("tries again the next time it opens");
        await Assert.That(_log).Contains("legacy_root_kept");
    }

    [Test]
    public async Task An_updater_that_does_not_exit_keeps_the_root() {
        _effects.Failing.Add("stop 11");

        var outcome = await Run();

        await Assert.That(_effects.Calls).DoesNotContain("root");
        await Assert.That(outcome.KeptBecause).IsEqualTo("Update.exe is still running");
    }

    /// <summary>The root stays under its name, but without its updater it can no longer update itself back over the
    /// kcap entry, so the entries that would run that updater go too.</summary>
    [Test]
    public async Task A_root_in_use_loses_its_updater_and_its_own_entries() {
        _effects.Failing.Add("root");

        var outcome = await Run();

        await Assert.That(_effects.Calls.TakeLast(3)).IsEquivalentTo(["root", "updater", "delete KurrentCapacitor"], CollectionOrdering.Matching);
        await Assert.That(outcome.KeptBecause).IsEqualTo($"a file in {Root} is in use");
    }

    [Test]
    public async Task A_root_whose_updater_cannot_be_deleted_keeps_its_entries() {
        _effects.Failing.Add("root");
        _effects.Failing.Add("updater");

        var outcome = await Run();

        await Assert.That(_effects.Calls).DoesNotContain("delete KurrentCapacitor");
        await Assert.That(outcome.Failures).Contains("its updater");
    }

    [Test]
    public async Task A_shortcut_that_cannot_be_deleted_does_not_stop_the_rest() {
        _effects.Failing.Add("shortcut");

        var outcome = await Run();

        await Assert.That(_effects.Calls).Contains("root");
        await Assert.That(outcome.KeptBecause).IsNull();
        await Assert.That(outcome.Failures).IsEquivalentTo([$"the shortcut {Shortcut}"]);
        await Assert.That(outcome.Message).Contains("Some of its leftovers remain");
    }

    sealed class FakeEffects : ILegacyRetirementEffects {
        public List<string> Calls { get; } = [];
        public HashSet<string> Failing { get; } = [];
        public string? RepointFailure { get; set; }

        void Call(string name) {
            Calls.Add(name);
            if (Failing.Contains(name)) throw new IOException($"{name} failed");
        }

        public bool EndProcess(RunningImage process) {
            var name = $"stop {process.Pid}";
            Calls.Add(name);
            return !Failing.Contains(name);
        }

        public Task<string?> RepointDaemonsAsync(string legacyRoot, CancellationToken ct) {
            Calls.Add("repoint");
            return Task.FromResult(RepointFailure);
        }

        public void DeleteShortcut(string path) => Call("shortcut");
        public void DeleteAutostart(string valueName) => Call("autostart");
        public void RepointUninstallEntry(string legacyRoot, string ownRoot) => Call("entry");
        public void MoveUserPath(string fromCli, string toCli) => Call("path");
        public void RemoveRoot(string root) => Call("root");
        public void RemoveUpdater(string root) => Call("updater");
        public void DeleteUninstallEntry(string key, string root) => Call($"delete {key}");
    }
}
