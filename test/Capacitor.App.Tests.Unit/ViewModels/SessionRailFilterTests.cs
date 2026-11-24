using System.Reactive.Linq;
using Capacitor.App.ViewModels;
using DynamicData;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class SessionRailFilterTests {
    static SessionRailViewModel Build(
            FakeAgentDirectory directory, bool scopeLocked = false, RailPreferences? preferences = null,
            Action<RailPreferences>? persist = null) =>
        new(directory, _ => { }, _ => { }, TimeProvider.System, preferences: preferences, persistPreferences: persist, scopeLocked: scopeLocked);

    static AgentRow Row(string id, string vendor) =>
        AgentRow.FromLocal(new(id, "agent", vendor, "/repo", "Running", null, null, null, DateTime.UtcNow, null, null),
            new RepoIdentity("path:/repo", "repo"));

    static string Shown(SessionRailViewModel rail) =>
        string.Join(",", rail.Groups.SelectMany(g => g.Sessions).Select(s => s.Id).Order(StringComparer.Ordinal));

    static RailHarnessOption Option(SessionRailViewModel rail, string vendor) => rail.Harnesses.Single(h => h.Vendor == vendor);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Filters_start_closed_and_toggle() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = Build(directory);
            var raised = new List<string?>();
            rail.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
            await Assert.That(rail.FiltersOpen).IsFalse();
            await rail.ToggleFiltersCommand.Execute();
            await Assert.That(rail.FiltersOpen).IsTrue();
            await rail.ToggleFiltersCommand.Execute();
            await Assert.That(rail.FiltersOpen).IsFalse();
            await Assert.That(raised.Count(n => n == nameof(SessionRailViewModel.FiltersOpen))).IsEqualTo(2);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Settings_differ_when_any_control_differs_from_the_defaults() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = Build(directory);
            var raised = 0;
            rail.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SessionRailViewModel.FiltersDiffer)) raised++; };
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Scope = RailScope.Team;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Scope = RailScope.Mine;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.State = RailState.Live;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.State = RailState.All;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Grouping = RailGrouping.Date;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Grouping = RailGrouping.Repository;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Sort = RailSort.Title;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Sort = RailSort.LastUpdated;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            rail.Descending = false;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            rail.Descending = true;
            await Assert.That(rail.FiltersDiffer).IsFalse();

            await Assert.That(raised).IsEqualTo(10);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Locked_scope_with_defaults_has_no_custom_settings() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = Build(directory, scopeLocked: true, preferences: RailPreferences.Default with { Scope = RailScope.Team });
            await Assert.That(rail.Scope).IsEqualTo(RailScope.Mine);
            await Assert.That(rail.FiltersDiffer).IsFalse();
        });
    }

    /// Unchecking a harness hides its rows; the All row brings every harness back, and from all shown
    /// it hides every one. The open session stays whatever the filter says.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Harness_checks_narrow_the_rows_and_all_toggles_every_harness() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate([Row("c1", "claude"), Row("x1", "codex"), Row("g1", "grok")]);
            using var rail = Build(directory);
            await Assert.That(rail.AllHarnessesChecked).IsTrue();
            await Assert.That(rail.HarnessLabel).IsEqualTo("All");
            await Assert.That(rail.HarnessMark).IsNull();

            Option(rail, "codex").IsChecked = false;
            await Assert.That(Shown(rail)).IsEqualTo("c1,g1");
            await Assert.That(rail.AllHarnessesChecked).IsNull();
            await Assert.That(rail.IsHarnessNarrowed).IsTrue();
            await Assert.That(Option(rail, "codex").IsChecked).IsFalse();

            await rail.ToggleAllHarnessesCommand.Execute();
            await Assert.That(Shown(rail)).IsEqualTo("c1,g1,x1");
            await Assert.That(rail.AllHarnessesChecked).IsTrue();

            await rail.ToggleAllHarnessesCommand.Execute();
            await Assert.That(Shown(rail)).IsEqualTo("");
            await Assert.That(rail.AllHarnessesChecked).IsFalse();
            await Assert.That(rail.HarnessLabel).IsEqualTo("None");
            await Assert.That(rail.Harnesses.Any(h => h.IsChecked)).IsFalse();

            rail.SelectedSessionKey = directory.Rows.Items.Single(r => r.Id == "x1").Key;
            await Assert.That(Shown(rail)).IsEqualTo("x1");

            await rail.ToggleAllHarnessesCommand.Execute();
            await Assert.That(rail.Harnesses.All(h => h.IsChecked)).IsTrue();
        });
    }

    /// Only shows its harness alone, and the trigger carries its mark; with several shown it counts them.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Only_shows_one_harness_and_the_trigger_names_or_counts_the_shown_ones() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate([Row("c1", "claude"), Row("x1", "codex"), Row("m1", "muse")]);
            using var rail = Build(directory);

            await Option(rail, "muse").OnlyCommand.Execute();
            await Assert.That(Shown(rail)).IsEqualTo("m1");
            await Assert.That(rail.HarnessLabel).IsEqualTo("Muse Code");
            await Assert.That(rail.HarnessMark).IsEqualTo("muse");
            await Assert.That(rail.HarnessTip).IsEqualTo("Harness: Muse Code");

            Option(rail, "claude").IsChecked = true;
            Option(rail, "codex").IsChecked = true;
            Option(rail, "grok").IsChecked = true;
            await Assert.That(rail.HarnessLabel).IsEqualTo("4");
            await Assert.That(rail.HarnessMark).IsNull();
        });
    }

    /// A vendor the registry does not list shows while every harness does, and hides once the list is narrowed.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_unlisted_vendor_shows_only_while_every_harness_does() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate([Row("c1", "claude"), Row("u1", "someday")]);
            using var rail = Build(directory);
            await Assert.That(Shown(rail)).IsEqualTo("c1,u1");

            Option(rail, "codex").IsChecked = false;
            await Assert.That(Shown(rail)).IsEqualTo("c1");
        });
    }

    /// Counts are per harness over what scope and state admit, and ignore the harness filter itself.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Harness_counts_follow_the_state_and_ignore_the_harness_filter() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            directory.Rows.AddOrUpdate([Row("c1", "claude"), Row("c2", "claude"), Row("x1", "codex")]);
            using var rail = Build(directory);
            await Assert.That(Option(rail, "claude").Count).IsEqualTo(2);
            await Assert.That(Option(rail, "codex").Count).IsEqualTo(1);
            await Assert.That(Option(rail, "grok").Count).IsEqualTo(0);

            Option(rail, "claude").IsChecked = false;
            await Assert.That(Option(rail, "claude").Count).IsEqualTo(2);

            rail.State = RailState.Ended;
            await Assert.That(Option(rail, "claude").Count).IsEqualTo(0);
        });
    }

    /// The hidden harnesses persist, count as custom settings, and come back from a saved set.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Hidden_harnesses_persist_and_apply() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            var persisted = new List<RailPreferences>();
            using var rail = Build(directory, persist: persisted.Add);

            Option(rail, "codex").IsChecked = false;
            Option(rail, "claude").IsChecked = false;
            await Assert.That(rail.FiltersDiffer).IsTrue();
            await Assert.That(persisted[^1].HiddenHarnesses).IsEquivalentTo(["claude", "codex"]);

            await rail.ToggleAllHarnessesCommand.Execute();
            await Assert.That(rail.FiltersDiffer).IsFalse();
            await Assert.That(persisted[^1]).IsEqualTo(RailPreferences.Default);

            rail.ApplyPreferences(RailPreferences.Default with { HiddenHarnesses = ["Grok", "not-a-harness"] });
            await Assert.That(Option(rail, "grok").IsChecked).IsFalse();
            await Assert.That(rail.Preferences.HiddenHarnesses).IsEquivalentTo(["grok"]);

            using var reopened = Build(directory, preferences: rail.Preferences);
            await Assert.That(Option(reopened, "grok").IsChecked).IsFalse();
            await Assert.That(reopened.AllHarnessesChecked).IsNull();
        });
    }
}
