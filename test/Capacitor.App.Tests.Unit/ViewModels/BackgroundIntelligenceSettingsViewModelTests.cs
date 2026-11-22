using Capacitor.Cli.Core;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Tests.Unit.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Setup.Intelligence;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// <summary>The Background intelligence tab shows what the server reports, saves only a change, never lets an older
/// answer or a read overwrite what the user is typing, and holds off, and is held off by, an upgrade or a search
/// change</summary>
[NotInParallel("AvaloniaSession")]
public class BackgroundIntelligenceSettingsViewModelTests {
    static IntelligenceSettings Settings(
            string mode = IntelligenceModes.LocalAgent, string? harness = "claude", string? vendor = null, string? model = null,
            bool outcomes = true, bool outcomesPinned = false, int limit = 300, bool limitPinned = false) =>
        new(mode, mode == IntelligenceModes.LocalAgent ? harness : null, vendor, model, outcomes, outcomesPinned, limit, limitPinned, ["titles"]);

    static IntelligenceSettings Provider(string vendor = "openai", string model = "gpt-5-mini") =>
        Settings(IntelligenceModes.Provider, vendor: vendor, model: model);

    static Task<IntelligenceSettingsOutcome> Answer(IntelligenceSettings settings) =>
        Task.FromResult<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Read(settings));

    sealed class Rig : IDisposable {
        public readonly FakeKcapCli Cli = new();
        public string[] Found = ["claude", "codex"];
        public IntelligenceSettings Server;
        public readonly LocalUpgradeController Upgrade;
        public readonly LocalSearchController Search;
        public readonly BackgroundIntelligenceSettingsViewModel Vm;

        public Rig(IntelligenceSettings? server = null) {
            Server = server ?? Settings();
            Cli.IntelligenceStatusBehavior = () => Answer(Server);
            Cli.IntelligencePreferencesBehavior = (outcomes, limit) => {
                Server = Server with {
                    ImportOutcomes = outcomes ?? Server.ImportOutcomes, BacklogSessionTouchesPerDay = limit ?? Server.BacklogSessionTouchesPerDay,
                };
                return Answer(Server);
            };
            // As the app wires them: each start is held while the other, or a tab operation, runs.
            Upgrade = new LocalUpgradeController(() => Cli, action => action(), held: () => Search!.Running || Vm!.Busy);
            Search  = new LocalSearchController(() => Cli, action => action(), held: () => Upgrade.Running || Vm!.Busy);
            Vm      = new BackgroundIntelligenceSettingsViewModel(Cli, _ => Task.FromResult(Found), Upgrade, Search);
        }

        public BackgroundIntelligenceOption Option(string token) => Vm.Options.Single(o => o.Token == token);

        public string? SelectedToken => Vm.Options.SingleOrDefault(o => o.IsSelected)?.Token;

        public void Dispose() => Vm.Dispose();
    }

    static Task Run(Func<Rig, Task> body, IntelligenceSettings? server = null) => AvaloniaSession.WithImmediateRxScheduler(async () => {
        using var rig = new Rig(server);
        await body(rig);
    });

    [Test]
    public Task A_read_fills_each_group() => Run(async rig => {
        await Assert.That(rig.Vm.IsLoading).IsFalse();
        await rig.Vm.ShownAsync();

        await Assert.That(rig.Vm.ShowsControls).IsTrue();
        await Assert.That(rig.SelectedToken).IsEqualTo("claude");
        await Assert.That(rig.Vm.Options.Select(o => o.Token)).IsEquivalentTo(
            ["claude", "codex", "pi", "opencode", "provider", "none"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
        await Assert.That(rig.Option("claude").Label).IsEqualTo("My local agent: Claude Code");
        await Assert.That(rig.Option("pi").NotFound).IsTrue();
        await Assert.That(rig.Option("codex").CanSelect).IsTrue();
        await Assert.That(rig.Vm.ImportOutcomes).IsTrue();
        await Assert.That(rig.Vm.DailyLimitText).IsEqualTo("300");
        await Assert.That(rig.Vm.CanToggleOutcomes).IsTrue();
        await Assert.That(rig.Vm.CanEditLimit).IsTrue();
    });

    [Test]
    public Task A_provider_reads_into_its_vendor_and_model() => Run(async rig => {
        await rig.Vm.ShownAsync();

        await Assert.That(rig.Vm.ProviderSelected).IsTrue();
        await Assert.That(rig.Vm.SelectedVendor).IsEqualTo(IntelligenceVendor.OpenAI);
        await Assert.That(rig.Vm.Model).IsEqualTo("gpt-5-mini");
        await Assert.That(rig.Vm.ApiKey).IsEqualTo("");
    }, Provider());

    [Test]
    public Task Custom_selects_nothing_and_says_where_it_was_set() => Run(async rig => {
        await rig.Vm.ShownAsync();

        await Assert.That(rig.SelectedToken).IsNull();
        await Assert.That(rig.Vm.CustomText).IsEqualTo(BackgroundIntelligenceSettingsViewModel.CustomNote);
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();
    }, Settings(IntelligenceModes.Custom));

    [Test]
    public Task A_configured_agent_that_is_not_found_stays_selected_and_marked() => Run(async rig => {
        rig.Found = ["claude"];
        await rig.Vm.ShownAsync();

        await Assert.That(rig.SelectedToken).IsEqualTo("codex");
        await Assert.That(rig.Option("codex").NotFound).IsTrue();
        await Assert.That(rig.Option("codex").CanSelect).IsFalse();
        await Assert.That(rig.Vm.WaitingText).IsEqualTo("Background work waits until Codex is back on this computer");
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();
    }, Settings(harness: "codex"));

    [Test]
    public Task A_pinned_setting_is_read_only_with_the_note() => Run(async rig => {
        await rig.Vm.ShownAsync();

        await Assert.That(rig.Vm.CanToggleOutcomes).IsFalse();
        await Assert.That(rig.Vm.OutcomesPinnedNote).IsEqualTo(BackgroundIntelligenceSettingsViewModel.PinnedNote);
        await Assert.That(rig.Vm.LimitReadOnly).IsTrue();
        await Assert.That(rig.Vm.CanEditLimit).IsFalse();
        await Assert.That(rig.Vm.LimitPinnedNote).IsEqualTo(BackgroundIntelligenceSettingsViewModel.PinnedNote);

        rig.Vm.DailyLimitText = "900";
        await rig.Vm.SaveLimitAsync();
        await rig.Vm.SetImportOutcomesAsync(false);
        await Assert.That(rig.Cli.IntelligencePreferenceCalls).IsEmpty();
    }, Settings(outcomesPinned: true, limitPinned: true));

    [Test]
    public Task Save_enables_only_on_a_change() => Run(async rig => {
        await rig.Vm.ShownAsync();
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();

        rig.Option("none").IsSelected = true;
        await Assert.That(rig.Vm.CanSaveMode).IsTrue();
        rig.Option("claude").IsSelected = true;
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();

        rig.Option("provider").IsSelected = true;
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();
        rig.Vm.ApiKey = "sk-test";
        await Assert.That(rig.Vm.CanSaveMode).IsTrue();

        await Assert.That(rig.Vm.CanSaveLimit).IsFalse();
        rig.Vm.DailyLimitText = "500";
        await Assert.That(rig.Vm.CanSaveLimit).IsTrue();
        rig.Vm.DailyLimitText = "100001";
        await Assert.That(rig.Vm.CanSaveLimit).IsFalse();
        await Assert.That(rig.Vm.LimitHint).IsEqualTo(BackgroundIntelligenceSettingsViewModel.LimitRangeHint);
        rig.Vm.DailyLimitText = "300";
        await Assert.That(rig.Vm.CanSaveLimit).IsFalse();
    });

    [Test]
    public Task A_preference_write_shows_what_the_server_answered() => Run(async rig => {
        await rig.Vm.ShownAsync();
        rig.Cli.IntelligencePreferencesBehavior = (_, _) => Answer(rig.Server with { BacklogSessionTouchesPerDay = 450 });

        rig.Vm.DailyLimitText = "500";
        await rig.Vm.SaveLimitAsync();

        await Assert.That(rig.Cli.IntelligencePreferenceCalls.Single()).IsEqualTo((null, 500));
        await Assert.That(rig.Vm.DailyLimitText).IsEqualTo("450");
        await Assert.That(rig.Vm.CanSaveLimit).IsFalse();
    });

    [Test]
    public Task A_refusal_leaves_the_shown_state_alone() => Run(async rig => {
        await rig.Vm.ShownAsync();
        rig.Cli.IntelligenceBehavior = _ => Task.FromResult<IntelligenceSetupOutcome>(new IntelligenceSetupOutcome.Refused("key_rejected: 401"));
        rig.Cli.IntelligencePreferencesBehavior = (_, _) =>
            Task.FromResult<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Refused("import_outcomes_pinned_above_store"));

        rig.Option("provider").IsSelected = true;
        rig.Vm.ApiKey = "sk-bad";
        await rig.Vm.SaveModeAsync();
        await rig.Vm.SetImportOutcomesAsync(false);

        await Assert.That(rig.Vm.ModeError).IsEqualTo("The provider refused the key: 401");
        await Assert.That(rig.SelectedToken).IsEqualTo("provider");
        await Assert.That(rig.Vm.ApiKey).IsEqualTo("sk-bad");
        await Assert.That(rig.Vm.ImportOutcomes).IsTrue();
        await Assert.That(rig.Vm.OutcomesError).IsEqualTo("The server's own environment decides whether imported sessions get outcomes");
    });

    [Test]
    public Task A_server_without_the_routes_hides_the_controls() => Run(async rig => {
        rig.Cli.IntelligenceStatusBehavior = () =>
            Task.FromResult<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Refused(IntelligenceSettingsClient.NeedsUpdate));

        await rig.Vm.ShownAsync();

        await Assert.That(rig.Vm.ShowsControls).IsFalse();
        await Assert.That(rig.Vm.NeedsUpdateText).IsEqualTo("The local server needs updating before it can show or change these settings");
    });

    [Test]
    public Task An_updated_server_shows_the_controls_on_the_next_read() => Run(async rig => {
        rig.Cli.IntelligenceStatusBehavior = () =>
            Task.FromResult<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Refused(IntelligenceSettingsClient.NeedsUpdate));
        await rig.Vm.ShownAsync();
        await Assert.That(rig.Vm.NeedsUpdate).IsTrue();

        rig.Cli.IntelligenceStatusBehavior = () => Answer(rig.Server);
        await rig.Vm.RefocusedAsync();

        await Assert.That(rig.Vm.NeedsUpdate).IsFalse();
        await Assert.That(rig.Vm.NeedsUpdateText).IsNull();
        await Assert.That(rig.Vm.ShowsControls).IsTrue();
    });

    [Test]
    public Task A_failed_read_offers_a_retry() => Run(async rig => {
        rig.Cli.IntelligenceStatusBehavior = () => Task.FromResult<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Failed("refused"));
        await rig.Vm.ShownAsync();

        await Assert.That(rig.Vm.ReadError).IsEqualTo("Could not read the settings: refused");
        await Assert.That(rig.Vm.CanRetry).IsTrue();

        rig.Cli.IntelligenceStatusBehavior = () => Answer(rig.Server);
        await rig.Vm.RefreshAsync();
        await Assert.That(rig.Vm.ReadError).IsNull();
        await Assert.That(rig.Vm.ShowsControls).IsTrue();
    });

    [Test]
    public Task A_read_that_lands_after_a_later_operation_is_dropped() => Run(async rig => {
        var first  = new TaskCompletionSource<IntelligenceSettingsOutcome>();
        var second = new TaskCompletionSource<IntelligenceSettingsOutcome>();
        var reads  = new Queue<TaskCompletionSource<IntelligenceSettingsOutcome>>([first, second]);
        rig.Cli.IntelligenceStatusBehavior = () => reads.Dequeue().Task;

        var shown     = rig.Vm.ShownAsync();
        await Assert.That(rig.Vm.IsLoading).IsTrue();
        var refocused = rig.Vm.RefocusedAsync();
        second.SetResult(new IntelligenceSettingsOutcome.Read(Settings(harness: "codex")));
        await refocused;
        first.SetResult(new IntelligenceSettingsOutcome.Read(Settings(harness: "claude", limit: 10)));
        await shown;

        await Assert.That(rig.SelectedToken).IsEqualTo("codex");
        await Assert.That(rig.Vm.DailyLimitText).IsEqualTo("300");
        await Assert.That(rig.Vm.Busy).IsFalse();
    });

    /// <summary>The model counts only for a provider, so the provider rows change only it.</summary>
    [Test]
    [Arguments(IntelligenceModes.LocalAgent, "claude", null, null, IntelligenceModes.LocalAgent, "codex", null, null)]
    [Arguments(IntelligenceModes.LocalAgent, "claude", null, null, IntelligenceModes.None, null, null, null)]
    [Arguments(IntelligenceModes.Provider, null, "openai", "gpt-5-mini", IntelligenceModes.Provider, null, "anthropic", "claude-sonnet-4-6")]
    [Arguments(IntelligenceModes.Provider, null, "openai", "gpt-5-mini", IntelligenceModes.Provider, null, "openai", "gpt-5")]
    public Task A_change_elsewhere_between_showing_and_saving_sends_nothing_and_shows_it(
            string mode, string? harness, string? vendor, string? model,
            string laterMode, string? laterHarness, string? laterVendor, string? laterModel) => Run(async rig => {
        await rig.Vm.ShownAsync();
        rig.Option("none").IsSelected = true;

        var later = Settings(laterMode, laterHarness, laterVendor, laterModel);
        rig.Server = later;
        await rig.Vm.SaveModeAsync();

        await Assert.That(rig.Cli.IntelligenceCalls).IsEmpty();
        await Assert.That(rig.Vm.ModeNotice).IsEqualTo(BackgroundIntelligenceSettingsViewModel.ReviewAgain);
        await Assert.That(rig.SelectedToken).IsEqualTo(laterMode switch {
            IntelligenceModes.LocalAgent => laterHarness,
            IntelligenceModes.Provider   => "provider",
            _                            => "none",
        });
        if (laterModel is not null) await Assert.That(rig.Vm.Model).IsEqualTo(laterModel);
    }, Settings(mode, harness, vendor, model));

    [Test]
    public Task A_mode_save_is_read_back() => Run(async rig => {
        await rig.Vm.ShownAsync();
        rig.Cli.IntelligenceBehavior = answer => {
            rig.Server = Settings(harness: answer.Harness) with { BacklogSessionTouchesPerDay = 120 };
            return Task.FromResult<IntelligenceSetupOutcome>(new IntelligenceSetupOutcome.Applied(new(answer.Mode, answer.Harness, null, true, 120, [])));
        };
        var readsBefore = rig.Cli.IntelligenceStatusCalls;

        rig.Option("codex").IsSelected = true;
        await rig.Vm.SaveModeAsync();

        await Assert.That(rig.Cli.IntelligenceCalls.Single().Answer).IsEqualTo(IntelligenceAnswer.LocalAgent("codex"));
        await Assert.That(rig.Cli.IntelligenceCalls.Single().ImportOutcomes).IsNull();
        await Assert.That(rig.Cli.IntelligenceStatusCalls - readsBefore).IsEqualTo(2);
        await Assert.That(rig.SelectedToken).IsEqualTo("codex");
        await Assert.That(rig.Vm.DailyLimitText).IsEqualTo("120");
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();
    });

    [Test]
    public Task A_failed_read_after_a_saved_mode_shows_the_saved_mode_with_the_error() => Run(async rig => {
        await rig.Vm.ShownAsync();
        rig.Cli.IntelligenceBehavior = answer => {
            rig.Cli.IntelligenceStatusBehavior = () => Task.FromResult<IntelligenceSettingsOutcome>(new IntelligenceSettingsOutcome.Failed("gone"));
            return Task.FromResult<IntelligenceSetupOutcome>(new IntelligenceSetupOutcome.Applied(new("none", null, null, true, 300, [])));
        };

        rig.Option("none").IsSelected = true;
        await rig.Vm.SaveModeAsync();

        await Assert.That(rig.SelectedToken).IsEqualTo("none");
        await Assert.That(rig.Vm.CanSaveMode).IsFalse();
        await Assert.That(rig.Vm.ReadError).IsEqualTo(BackgroundIntelligenceSettingsViewModel.ReadBackFailed);
        await Assert.That(rig.Vm.CanRetry).IsTrue();
        await Assert.That(rig.Vm.CanEditMode).IsTrue();
        await Assert.That(rig.Vm.CanToggleOutcomes).IsFalse();
        await Assert.That(rig.Vm.CanEditLimit).IsFalse();
    });

    [Test]
    public Task A_refocus_read_keeps_a_draft_and_flags_a_changed_value_under_it() => Run(async rig => {
        await rig.Vm.ShownAsync();
        rig.Vm.Model  = "gpt-5";
        rig.Vm.ApiKey = "sk-typed";
        rig.Vm.DailyLimitText = "800";

        rig.Server = Provider(model: "gpt-4.1") with { BacklogSessionTouchesPerDay = 50 };
        await rig.Vm.RefocusedAsync();

        await Assert.That(rig.Vm.Model).IsEqualTo("gpt-5");
        await Assert.That(rig.Vm.ApiKey).IsEqualTo("sk-typed");
        await Assert.That(rig.Vm.DailyLimitText).IsEqualTo("800");
        await Assert.That(rig.Vm.ModeChangedText).IsEqualTo("Changed elsewhere to OpenAI (gpt-4.1)");
        await Assert.That(rig.Vm.LimitChangedText).IsEqualTo("Changed elsewhere to 50");

        rig.Vm.RevertLimitCommand.Execute().Subscribe();
        await Assert.That(rig.Vm.DailyLimitText).IsEqualTo("50");
        await Assert.That(rig.Vm.LimitChangedText).IsNull();
    }, Provider());

    [Test]
    public Task The_tab_starts_nothing_while_an_upgrade_or_a_search_change_runs() => Run(async rig => {
        await rig.Vm.ShownAsync();
        var reads = rig.Cli.IntelligenceStatusCalls;
        var upgrade = new TaskCompletionSource<StreamingResult>();
        rig.Cli.LocalUpgradeBehavior = (_, _, _) => upgrade.Task;
        await rig.Upgrade.StartAsync(CancellationToken.None);

        rig.Vm.DailyLimitText = "500";
        await rig.Vm.SaveLimitAsync();
        await rig.Vm.RefocusedAsync();

        await Assert.That(rig.Vm.HeldText).IsEqualTo(LocalSearchViewModel.BusyText);
        await Assert.That(rig.Vm.CanSaveLimit).IsFalse();
        await Assert.That(rig.Cli.IntelligencePreferenceCalls).IsEmpty();
        await Assert.That(rig.Cli.IntelligenceStatusCalls).IsEqualTo(reads);

        upgrade.SetResult(new StreamingResult(3, false, []));
        await rig.Upgrade.SettleAsync();
        await Assert.That(rig.Vm.HeldText).IsNull();

        var search = new TaskCompletionSource<StreamingResult>();
        rig.Cli.SetupLocalBehavior = (_, _, _) => search.Task;
        await rig.Search.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
        await rig.Vm.SaveLimitAsync();
        await Assert.That(rig.Cli.IntelligencePreferenceCalls).IsEmpty();
        search.SetResult(new StreamingResult(0, false, []));
        await rig.Search.SettleAsync();
    });

    [Test]
    public Task An_upgrade_and_a_search_change_are_held_while_a_tab_operation_runs() => Run(async rig => {
        var read = new TaskCompletionSource<IntelligenceSettingsOutcome>();
        rig.Cli.IntelligenceStatusBehavior = () => read.Task;

        var shown = rig.Vm.ShownAsync();
        await rig.Upgrade.StartAsync(CancellationToken.None);
        await rig.Search.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);

        await Assert.That(rig.Upgrade.Running).IsFalse();
        await Assert.That(rig.Search.Running).IsFalse();
        await Assert.That(rig.Cli.LocalUpgradeCallCount).IsEqualTo(0);
        await Assert.That(rig.Cli.SetupLocalCallCount).IsEqualTo(0);

        read.SetResult(new IntelligenceSettingsOutcome.Read(rig.Server));
        await shown;
        await Assert.That(rig.Vm.Busy).IsFalse();
    });
}
