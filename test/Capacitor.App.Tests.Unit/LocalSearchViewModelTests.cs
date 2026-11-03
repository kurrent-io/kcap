using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using TUnit.Assertions.Enums;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LocalSearchViewModelTests {
    const string BuiltinConfigured = "{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"builtin\"}";
    const string Reused = "{\"v\":1,\"event\":\"reused\",\"server_url\":\"http://localhost:5000\",\"version\":\"1.2.0\"}";

    sealed class Harness : IDisposable {
        public readonly FakeKcapCli Cli = new();
        public readonly BehaviorSubject<ServerLaneStatus> Status = new(new(ServerLaneState.Connected));
        public readonly LocalSearchController Search;
        public readonly LocalUpgradeController Upgrade;
        public readonly LocalSearchViewModel Vm;
        public int Reconnects;
        /// What `data/embedding.json` holds; the view model reads it again once a run settles.
        public LocalEmbeddingSetting? Setting;
        /// What the installed stack's manifest offers; looked up again on every reconnect.
        public LocalEmbeddingModelManifest? Offered;
        /// Set before construction to hold the lookup open.
        public TaskCompletionSource<LocalEmbeddingModelManifest?>? Lookup;

        public int SettingsOpened;
        public readonly BehaviorSubject<Action?> OpenSettings = new(null);
        /// What the server's embeddings report answers; asked again on every reconnect.
        public EmbeddingHealthAnswer? Health;
        public int HealthReads;

        public Harness(LocalEmbeddingSetting? setting, bool offered = true, TaskCompletionSource<LocalEmbeddingModelManifest?>? lookup = null,
                EmbeddingHealthAnswer? health = null, IScheduler? reads = null) {
            Lookup  = lookup;
            Setting = setting;
            Health  = health;
            Offered = offered ? new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]) : null;
            Search  = new LocalSearchController(() => Cli, action => action(), held: () => Upgrade.Running);
            Upgrade = new LocalUpgradeController(() => Cli, action => action(), held: () => Search.Running);
            Vm = new LocalSearchViewModel(() => Setting, _ => Lookup?.Task ?? Task.FromResult(Offered), Status, Search, Upgrade, () => {
                Reconnects++;
                return Task.CompletedTask;
            }, CancellationToken.None, reads ?? ImmediateScheduler.Instance, openSettings: OpenSettings, readHealth: _ => {
                HealthReads++;
                return Task.FromResult(Health);
            });
        }

        public async Task ApplyAsync(int exitCode, params string[] lines) {
            Cli.SetupLocalBehavior = LocalSetupScript.Prints(exitCode, lines);
            await Vm.ApplyCommand.Execute().ToTask();
            await Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose() => Vm.Dispose();
    }

    [Test]
    public async Task The_rail_names_how_the_server_searches() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var keyword = new Harness(null);
            using var meaning = new Harness(LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

            await Assert.That(keyword.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(meaning.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(meaning.Vm.CurrentText).IsEqualTo("Searches by meaning with mxbai-embed-large.");
        });
    }

    /// <summary>The rail's search word is the way into the Settings tab that changes it.</summary>
    [Test]
    public async Task The_rail_word_opens_search_settings() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            h.OpenSettings.OnNext(() => h.SettingsOpened++);

            await h.Vm.OpenSettingsCommand.Execute().ToTask();

            await Assert.That(h.SettingsOpened).IsEqualTo(1);
        });
    }

    /// <summary>A local server reached by URL with no profile has no Settings window, so the rail word opens the
    /// controls in place instead of linking to a window that cannot open.</summary>
    [Test]
    public async Task Without_a_settings_window_the_rail_word_opens_the_controls_in_place() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);

            await Assert.That(await h.Vm.OpenSettingsCommand.CanExecute.FirstAsync()).IsFalse();
            await Assert.That(h.Vm.ShowsInlineSettings).IsTrue();
            await Assert.That(h.Vm.ShowsSettingsLink).IsFalse();

            h.OpenSettings.OnNext(() => h.SettingsOpened++);

            await Assert.That(await h.Vm.OpenSettingsCommand.CanExecute.FirstAsync()).IsTrue();
            await Assert.That(h.Vm.ShowsInlineSettings).IsFalse();
            await Assert.That(h.Vm.ShowsSettingsLink).IsTrue();
        });
    }

    [Test]
    public async Task The_builtin_option_follows_the_installed_stack_and_is_looked_up_again_on_reconnect() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null, offered: false);

            await Assert.That(h.Vm.Choice.BuiltinEnabled).IsFalse();
            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(h.Vm.Choice.Choice.Args).IsEmpty();

            h.Offered = new("builtin-test", 1, new('e', 64), [new("model.onnx", new('f', 64), 300L * 1024 * 1024)]);
            h.Status.OnNext(new(ServerLaneState.Retrying));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.Choice.BuiltinEnabled).IsTrue();
            await Assert.That(h.Vm.Choice.UseBuiltin).IsTrue();
        });
    }

    [Test]
    public async Task Builtin_cannot_be_applied_as_a_new_choice_while_the_lookup_is_pending() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var lookup = new TaskCompletionSource<LocalEmbeddingModelManifest?>();
            using var h = new Harness(LocalEmbeddingSetting.Declined, lookup: lookup);

            h.Vm.Choice.UseBuiltin = true;

            await Assert.That(h.Vm.Choice.BuiltinEnabled).IsFalse();
            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            var offered = h.Vm.Choice.WhenAnyValue(x => x.BuiltinEnabled).Where(enabled => enabled).FirstAsync().ToTask();
            lookup.SetResult(h.Offered);
            await offered.WaitAsync(TimeSpan.FromSeconds(5));
            h.Vm.Choice.UseBuiltin = true;

            await Assert.That(h.Vm.Choice.Choice.Args).IsEquivalentTo(["--builtin-embeddings"]);
            await Assert.That(h.Vm.CanApply).IsTrue();

            using var recorded = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)), lookup: new());
            await Assert.That(recorded.Vm.Choice.BuiltinEnabled).IsTrue();
            await Assert.That(recorded.Vm.Choice.UseBuiltin).IsTrue();
        });
    }

    /// While the lookup is open the shown recommendation is keyword search, yet setup left to decide could pick the
    /// built-in model, so nothing applies until the recommendation is the one setup will reach.
    [Test]
    public async Task An_unchosen_recommendation_waits_for_the_builtin_lookup() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var lookup = new TaskCompletionSource<LocalEmbeddingModelManifest?>();
            using var h = new Harness(null, lookup: lookup);

            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            var offered = h.Vm.WhenAnyValue(x => x.CanApply).Where(can => can).FirstAsync().ToTask();
            lookup.SetResult(h.Offered);
            await offered.WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.Choice.UseBuiltin).IsTrue();
            await Assert.That(h.Vm.Choice.Choice.Unchosen).IsTrue();
            await Assert.That(h.Vm.Choice.Choice.Args).IsEmpty();
        });
    }

    [Test]
    public async Task The_recorded_choice_cannot_be_applied_again() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

            await Assert.That(h.Vm.Choice.UseDefault).IsTrue();
            await Assert.That(h.Vm.CanApply).IsFalse();

            h.Vm.Choice.UseKeyword = true;

            await Assert.That(h.Vm.CanApply).IsTrue();
        });
    }

    /// <summary>No record is how a fresh install searches already, by keyword, while a recorded change that was never
    /// applied still has something to save.</summary>
    [Test]
    public async Task Keyword_search_with_nothing_recorded_has_nothing_to_save() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var fresh   = new Harness(null, offered: false);
            using var pending = new Harness(LocalEmbeddingSetting.Declined with { Pending = true }, offered: false);

            await Assert.That(fresh.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(fresh.Vm.CanApply).IsFalse();
            await Assert.That(pending.Vm.Choice.UseKeyword).IsTrue();
            await Assert.That(pending.Vm.CanApply).IsTrue();

            fresh.Vm.Choice.UseOllama = true;

            await Assert.That(fresh.Vm.CanApply).IsTrue();
        });
    }

    /// <summary>Setup records the new model as pending and restarts the server mid-run, so the reconnect reads a pending
    /// built-in setting while the change is still being applied.</summary>
    [Test]
    public async Task A_change_being_applied_reads_as_applying_rather_than_unapplied_or_failed() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Declined);
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            Action<StreamedLine> print = _ => { };
            h.Cli.SetupLocalBehavior = (onLine, _, _) => {
                print = onLine;
                return exit.Task;
            };
            h.Vm.Choice.UseBuiltin = true;

            await h.Vm.ApplyCommand.Execute().ToTask();
            h.Setting = LocalEmbeddingSetting.Builtin(new string('a', 64)) with { Pending = true };
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.StateText).IsEqualTo("setting up search");
            await Assert.That(h.Vm.CurrentText).IsEqualTo("Search changes are being applied.");
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo("Save");
            await Assert.That(h.Vm.CanApply).IsFalse();

            h.Setting = LocalEmbeddingSetting.Builtin(new string('a', 64));
            foreach (var line in new[] { BuiltinConfigured, Reused, LocalSetupScript.Committed })
                print(new StreamedLine(ProcessStreamKind.Stdout, line));
            exit.SetResult(new StreamingResult(0, false, []));
            await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.CurrentText).IsEqualTo("Searches by meaning with the built-in model.");
        });
    }

    /// <summary>A run's outcome describes the setting it left; once a terminal setup changes that setting, the run's
    /// summary and its note about the index being built say nothing true. Pinned for a run that turned the built-in
    /// model on and is then switched to keywords, and for a failed download, which leaves keyword search, then
    /// switched to a model.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task A_setting_changed_elsewhere_retires_the_last_runs_summary(bool configured) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Declined);
            h.Vm.Choice.UseBuiltin = true;
            var builtin = LocalEmbeddingSetting.Builtin(new string('a', 64));

            if (configured) h.Setting = builtin;
            await h.ApplyAsync(0,
                configured ? BuiltinConfigured
                    : """{"v":1,"event":"embeddings_skipped","code":"model_download_failed","message":"The built-in search model could not be downloaded"}""",
                Reused, LocalSetupScript.Committed);

            await Assert.That(h.Vm.Summary).IsNotNull();
            await Assert.That(h.Vm.ReembedNote is not null).IsEqualTo(configured);

            h.Setting = configured ? LocalEmbeddingSetting.Declined : builtin;
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.CurrentText).Contains(configured ? "keyword" : "meaning");
            await Assert.That(h.Vm.Summary).IsNull();
            await Assert.That(h.Vm.ReembedNote).IsNull();
        });
    }

    /// <summary>Holds each scheduled read until <see cref="RunAll"/>, so a test decides which read lands first.</summary>
    sealed class HeldReads : IScheduler {
        readonly Queue<Action> _held = new();

        public DateTimeOffset Now => DateTimeOffset.UtcNow;

        public IDisposable Schedule<TState>(TState state, Func<IScheduler, TState, IDisposable> action) {
            _held.Enqueue(() => action(this, state));
            return System.Reactive.Disposables.Disposable.Empty;
        }

        public IDisposable Schedule<TState>(TState state, TimeSpan dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

        public IDisposable Schedule<TState>(TState state, DateTimeOffset dueTime, Func<IScheduler, TState, IDisposable> action) => Schedule(state, action);

        public void RunAll() {
            while (_held.TryDequeue(out var read)) read();
        }
    }

    /// <summary>A successful run's own read checks the model files, so a terminal setup can change the setting and its
    /// reconnect read land first. The run's outcome shows until a read lands, then retires against what the run
    /// recorded, not against whichever read came first.</summary>
    [Test]
    public async Task An_outside_change_read_ahead_of_the_runs_own_retires_the_runs_summary() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var reads = new HeldReads();
            using var h = new Harness(LocalEmbeddingSetting.Declined, reads: reads);
            reads.RunAll();
            h.Vm.Choice.UseBuiltin = true;

            await h.ApplyAsync(0, BuiltinConfigured, Reused, LocalSetupScript.Committed);

            await Assert.That(h.Vm.Summary).IsEqualTo("Search by meaning with the built-in model.");

            h.Setting = LocalEmbeddingSetting.Ollama("mxbai-embed-large");
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));
            reads.RunAll();

            await Assert.That(h.Vm.CurrentText).IsEqualTo("Searches by meaning with mxbai-embed-large.");
            await Assert.That(h.Vm.Summary).IsNull();
            await Assert.That(h.Vm.ReembedNote).IsNull();
        });
    }

    [Test]
    public async Task A_recorded_builtin_model_reads_as_search_by_meaning_and_only_a_change_applies() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)));

            await Assert.That(h.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(h.Vm.CurrentText).IsEqualTo("Searches by meaning with the built-in model.");
            await Assert.That(h.Vm.CanApply).IsFalse();

            h.Vm.Choice.UseKeyword = true;

            await Assert.That(h.Vm.CanApply).IsTrue();
        });
    }

    [Test]
    [Arguments(0, true)]
    [Arguments(0, false)]
    [Arguments(1, true)]
    [Arguments(1, false)]
    public async Task An_upgrade_search_warning_allows_the_unchanged_builtin_choice_until_setup_succeeds(int exitCode, bool configured) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)));
            var canApply = false;
            using var subscription = h.Vm.WhenAnyValue(x => x.CanApply).Subscribe(value => canApply = value);
            const string skipped = """{"v":1,"event":"embeddings_skipped","code":"model_download_failed","message":"Model download failed"}""";
            h.Cli.LocalUpgradeBehavior = UpgradeScript.Prints(0,
                UpgradeScript.Planned("1.2.0", "1.3.0"), skipped, UpgradeScript.Upgraded("1.2.0", "1.3.0"));

            await h.Upgrade.StartAsync(CancellationToken.None);
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(h.Vm.Choice.UseBuiltin).IsTrue();
            await Assert.That(canApply).IsTrue();
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo("Try again");
            await Assert.That(h.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(h.Vm.CurrentText).Contains("keyword");

            await h.ApplyAsync(exitCode,
                configured ? """{"v":1,"event":"embeddings_configured","provider":"builtin"}""" : skipped,
                exitCode == 0 ? LocalSetupScript.Committed : LocalSetupScript.Failed("start_failed"));

            await Assert.That(h.Cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.Builtin], CollectionOrdering.Matching);
            var succeeded = exitCode == 0 && configured;
            await Assert.That(canApply).IsEqualTo(!succeeded);
            await Assert.That(h.Vm.ApplyLabel).IsEqualTo(succeeded ? "Save" : "Try again");
            await Assert.That(h.Vm.StateText).IsEqualTo(succeeded ? "search by meaning" : "keyword search");
        });
    }

    [Test]
    public async Task Turning_search_by_meaning_on_reruns_setup_with_the_model_and_reconnects() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Declined);
            h.Vm.Choice.UseOllama = true;

            h.Setting = LocalEmbeddingSetting.Ollama("mxbai-embed-large");
            await h.ApplyAsync(0,
                "{\"v\":1,\"event\":\"embeddings_configured\",\"provider\":\"ollama\",\"model\":\"mxbai-embed-large\"}",
                Reused, LocalSetupScript.Committed);

            await Assert.That(h.Cli.SetupLocalEmbeddings).IsEquivalentTo([LocalEmbeddingChoice.Default], CollectionOrdering.Matching);
            await Assert.That(h.Vm.Summary).IsEqualTo("Search by meaning with mxbai-embed-large.");
            await Assert.That(h.Vm.StateText).IsEqualTo("search by meaning");
            await Assert.That(h.Vm.FailureText).IsNull();
            await Assert.That(h.Reconnects).IsEqualTo(1);
        });
    }

    [Test]
    public async Task A_run_another_kcap_holds_off_says_so() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            h.Vm.Choice.UseDefault = true;

            await h.ApplyAsync(1, LocalSetupScript.Failed("lifecycle_busy"));

            await Assert.That(h.Vm.FailureText).IsEqualTo(LocalSearchViewModel.BusyText);
            await Assert.That(h.Reconnects).IsEqualTo(0);
        });
    }

    [Test]
    public async Task Nothing_can_be_applied_while_an_upgrade_runs() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.LocalUpgradeBehavior = (_, _, _) => exit.Task;
            h.Vm.Choice.UseDefault = true;

            await h.Upgrade.StartAsync(CancellationToken.None);
            var during = h.Vm.CanApply;
            exit.SetResult(new StreamingResult(0, false, []));
            await h.Upgrade.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(during).IsFalse();
            await Assert.That(h.Vm.CanApply).IsTrue();
        });
    }

    [Test]
    public async Task An_upgrade_does_not_start_while_a_search_change_runs() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(null);
            var exit = new TaskCompletionSource<StreamingResult>(TaskCreationOptions.RunContinuationsAsynchronously);
            h.Cli.SetupLocalBehavior = (_, _, _) => exit.Task;
            h.Vm.Choice.UseDefault = true;

            await h.Vm.ApplyCommand.Execute().ToTask();
            await h.Upgrade.StartAsync(CancellationToken.None);
            var upgrading = h.Upgrade.Running;
            exit.SetResult(new StreamingResult(0, false, []));
            await h.Search.SettleAsync().WaitAsync(TimeSpan.FromSeconds(5));

            await Assert.That(upgrading).IsFalse();
            await Assert.That(h.Cli.LocalUpgradeCallCount).IsEqualTo(0);
        });
    }

    /// A setup run from a terminal changes the record and restarts the server; nothing else tells the app.
    [Test]
    public async Task A_reconnect_rereads_a_setting_changed_elsewhere() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Ollama("mxbai-embed-large"));

            h.Setting = LocalEmbeddingSetting.Declined;
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            var before = h.Vm.StateText;
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(before).IsEqualTo("search by meaning");
            await Assert.That(h.Vm.StateText).IsEqualTo("keyword search");
            await Assert.That(h.Vm.Choice.UseKeyword).IsTrue();
        });
    }

    static readonly DateTimeOffset FailedAt = new(2026, 11, 2, 9, 14, 5, TimeSpan.Zero);

    static EmbeddingHealthAnswer Builtin(VectorStorageState state, bool usable = true) =>
        new("builtin", usable, usable ? null : "load_failed", new EmbeddingVectors(state, null, FailedAt));

    /// <summary>A usable model whose last pass stored nothing is the one state the recorded setting cannot show: it
    /// reads as attention beside the setting's unchanged description.</summary>
    [Test]
    public async Task Session_vectors_that_are_not_stored_need_attention() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)), health: Builtin(VectorStorageState.Failing));

            await Assert.That(h.Vm.VectorsFailing).IsTrue();
            await Assert.That(h.Vm.VectorsWarning).Contains("falls back to keywords");
            await Assert.That(h.Vm.VectorsFailedText).StartsWith("Last failed ");
            await Assert.That(h.Vm.CurrentText).IsEqualTo("Searches by meaning with the built-in model.");
        });
    }

    public static IEnumerable<Func<EmbeddingHealthAnswer?>> Settled() => [
        () => Builtin(VectorStorageState.Ok),
        () => Builtin(VectorStorageState.NotRun),
        () => Builtin(VectorStorageState.Unknown),
        () => Builtin(VectorStorageState.Failing, usable: false),
        () => new("builtin", true, null),
        () => null,
    ];

    /// <summary>A pass that worked or has not run, a state this build does not know, an older server, no answer, and a
    /// model that cannot embed at all, which the setting's own retry covers, all leave the page as it was.</summary>
    [Test]
    [MethodDataSource(nameof(Settled))]
    public async Task Every_other_report_leaves_the_search_page_as_it_was(EmbeddingHealthAnswer? health) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)), health: health);

            await Assert.That(h.Vm.VectorsFailing).IsFalse();
            await Assert.That(h.Vm.VectorsWarning).IsNull();
            await Assert.That(h.Vm.VectorsFailedText).IsNull();
            await Assert.That(h.Vm.StateText).IsEqualTo("search by meaning");
        });
    }

    /// <summary>A pass fails some time after the server comes up, so a reconnect and each showing of the controls ask
    /// again.</summary>
    [Test]
    public async Task A_reconnect_or_a_showing_rereads_the_vectors_state() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var h = new Harness(LocalEmbeddingSetting.Builtin(new string('a', 64)), health: Builtin(VectorStorageState.NotRun));

            h.Health = Builtin(VectorStorageState.Failing);
            h.Status.OnNext(new(ServerLaneState.Retrying, "Connection refused"));
            h.Status.OnNext(new(ServerLaneState.Connected));

            await Assert.That(h.Vm.VectorsFailing).IsTrue();

            h.Health = Builtin(VectorStorageState.Ok);
            h.Vm.ReadHealth();

            await Assert.That(h.Vm.VectorsFailing).IsFalse();
            await Assert.That(h.HealthReads).IsEqualTo(3);
        });
    }
}
