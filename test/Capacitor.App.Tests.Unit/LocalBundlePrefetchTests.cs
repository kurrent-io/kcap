using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Avalonia.Threading;
using Capacitor.App.Services.LocalSetup;
using Capacitor.App.Services.Onboarding;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.Core.Telemetry;

namespace Capacitor.App.Tests.Unit;

public class LocalBundlePrefetchTests {
    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    static SetupFunnel Funnel() => CliTelemetry.Disabled(TimeProvider.System).Funnel;

    /// A fetch that prints what it is given and runs until interrupted, recording that it was.
    sealed class HeldFetch {
        public readonly TaskCompletionSource Interrupted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly TaskCompletionSource Spawned = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action<StreamedLine>? Print;

        public FakeKcapCli Cli() => new() {
            LocalFetchBehavior = async (onLine, interrupt) => {
                Print = onLine;
                Spawned.TrySetResult();
                await using (interrupt.Register(() => Interrupted.TrySetResult())) {
                    await Task.WhenAny(Interrupted.Task, Release.Task);
                }
                return new StreamingResult(interrupt.IsCancellationRequested ? 130 : 0, false, []);
            },
        };
    }

    [Test]
    public async Task Start_runs_one_fetch_however_often_it_is_called() {
        var cli = new FakeKcapCli();
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        prefetch.Start();
        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);
        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(cli.LocalFetchCallCount).IsEqualTo(1);
    }

    /// A fetch that prints one failed line and exits, once <paramref name="release"/> completes.
    static FakeKcapCli FetchFailing(string code, int exit, Task? release = null) => new() {
        LocalFetchBehavior = async (onLine, _) => {
            if (release is not null) await release;
            onLine(new StreamedLine(ProcessStreamKind.Stdout, LocalSetupScript.Failed(code)));
            return new StreamingResult(exit, false, []);
        },
    };

    [Test]
    public async Task A_fetch_that_finds_no_published_bundle_says_so_once() {
        var cli = FetchFailing("bundle_not_published", LocalBundlePrefetch.NotPublishedExit);
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);
        var found = 0;
        prefetch.NotPublishedFound += () => Interlocked.Increment(ref found);

        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(prefetch.NotPublished).IsTrue();
        await Assert.That(found).IsEqualTo(1);
    }

    /// A download that failed, a signature that did not verify and an interrupted fetch say nothing about what is
    /// published, so none of them steers the wizard away from the local edition.
    [Test]
    [Arguments("download_failed", 1)]
    [Arguments("verification_failed", 1)]
    [Arguments("cancelled", 130)]
    public async Task A_fetch_that_fails_otherwise_is_not_taken_for_an_unpublished_bundle(string code, int exit) {
        var cli = FetchFailing(code, exit);
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);
        var found = 0;
        prefetch.NotPublishedFound += () => Interlocked.Increment(ref found);

        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(prefetch.NotPublished).IsFalse();
        await Assert.That(found).IsEqualTo(0);
    }

    /// The fetch reports while the page already leads with Local. On Windows the card stops leading and says why; off
    /// Windows the page stays as it was, and a failed download changes it nowhere.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true, "bundle_not_published", LocalBundlePrefetch.NotPublishedExit, true)]
    [Arguments(false, "bundle_not_published", LocalBundlePrefetch.NotPublishedExit, false)]
    [Arguments(true, "download_failed", 1, false)]
    public async Task A_bundle_found_unpublished_on_the_edition_page_is_explained_only_on_windows(
            bool windows, string code, int exit, bool explained) {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cli = FetchFailing(code, exit, release.Task);
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        var (before, after, reason, canChoose) = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root) { LocalRoot = new LocalRoot(Config.PathTo("local")) };
            var graph = WizardComposition.BuildGraph(harness.Options() with {
                Prefetch                  = prefetch,
                ExplainsUnpublishedBundle = windows,
                Bridges = WizardComposition.BuildBridges(
                    action => Dispatcher.UIThread.Post(action), CliTelemetry.Disabled(TimeProvider.System), AuthEndpoints.Defaults),
            });
            await graph.ViewModel.PendingEnterForTesting;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();
            var before = edition.LocalPreselected;

            release.SetResult();
            await prefetch.Completion.WaitAsync(Wait);
            Dispatcher.UIThread.RunJobs();

            return (before, edition.LocalPreselected, edition.LocalUnavailableReason, await edition.ChooseLocalCommand.CanExecute.FirstAsync());
        });

        await Assert.That(before).IsTrue();
        await Assert.That(after).IsEqualTo(!explained);
        await Assert.That(reason).IsEqualTo(explained ? EditionStepViewModel.NotPublishedOnWindows : null);
        await Assert.That(canChoose).IsEqualTo(!explained);
    }

    /// An install that found no bundle sends the user back to an edition page that no longer offers Local, on Windows.
    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true, true)]
    [Arguments(false, false)]
    public async Task An_install_that_found_no_bundle_leaves_local_unavailable_only_on_windows(bool windows, bool explained) {
        var (reason, preselected) = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root) { LocalRoot = new LocalRoot(Config.PathTo("local")) };
            harness.Cli.SetupLocalBehavior = LocalSetupScript.Prints(1, LocalSetupScript.Failed("bundle_not_published"));
            var graph = WizardComposition.BuildGraph(harness.Options() with { ExplainsUnpublishedBundle = windows });
            await graph.ViewModel.PendingEnterForTesting;
            var edition = graph.Steps.OfType<EditionStepViewModel>().Single();

            var settled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            graph.Install.Settled += _ => settled.TrySetResult();
            await graph.Install.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
            await settled.Task.WaitAsync(Wait);
            await edition.OnEnterAsync(CancellationToken.None);

            return (edition.LocalUnavailableReason, edition.LocalPreselected);
        });

        await Assert.That(reason).IsEqualTo(explained ? EditionStepViewModel.NotPublishedOnWindows : null);
        await Assert.That(preselected).IsEqualTo(!explained);
    }

    [Test]
    public async Task A_fetch_that_throws_settles_without_faulting() {
        var cli = new FakeKcapCli { LocalFetchBehavior = (_, _) => throw new InvalidOperationException("no spawn") };
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        prefetch.Start();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(prefetch.Completion.IsCompletedSuccessfully).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task An_available_local_edition_with_nothing_installed_is_preselected_and_fetched() {
        var fetch = new HeldFetch();
        var prefetch = new LocalBundlePrefetch(fetch.Cli, CancellationToken.None);

        var preselected = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(Funnel(), () => null, leadWithLocal: () => true, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            return edition.LocalPreselected;
        });
        await fetch.Spawned.Task.WaitAsync(Wait);
        fetch.Release.SetResult();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(preselected).IsTrue();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(true, false, false)]
    [Arguments(false, true, false)]
    [Arguments(false, false, true)]
    public async Task Nothing_is_preselected_or_fetched_where_local_cannot_or_need_not_be_set_up(
            bool unavailable, bool locked, bool installed) {
        var cli = new FakeKcapCli();
        var prefetch = new LocalBundlePrefetch(() => cli, CancellationToken.None);

        var preselected = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(
                Funnel(), () => unavailable ? EditionStepViewModel.UnsupportedPlatform : null, () => locked,
                leadWithLocal: () => !installed, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            return edition.LocalPreselected;
        });

        await Assert.That(preselected).IsFalse();
        await Assert.That(prefetch.Started).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(EditionChoice.Cloud)]
    [Arguments(EditionChoice.ExistingWorkspace)]
    public async Task Choosing_a_cloud_workspace_stops_the_fetch(EditionChoice choice) {
        var fetch = new HeldFetch();
        var prefetch = new LocalBundlePrefetch(fetch.Cli, CancellationToken.None);

        var chosen = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(Funnel(), () => null, leadWithLocal: () => true, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            await fetch.Spawned.Task.WaitAsync(Wait);
            var command = choice == EditionChoice.Cloud ? edition.ChooseCloudCommand : edition.SignInCommand;
            await command.Execute().ToTask();
            return edition.Choice;
        });

        await fetch.Interrupted.Task.WaitAsync(Wait);
        await prefetch.Completion.WaitAsync(Wait);
        await Assert.That(chosen).IsEqualTo(choice);
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Choosing_local_leaves_the_fetch_running() {
        var fetch = new HeldFetch();
        var prefetch = new LocalBundlePrefetch(fetch.Cli, CancellationToken.None);

        var running = await AvaloniaSession.DispatchAsync(async () => {
            var edition = new EditionStepViewModel(Funnel(), () => null, leadWithLocal: () => true, prefetch: prefetch);
            await edition.OnEnterAsync(CancellationToken.None);
            await fetch.Spawned.Task.WaitAsync(Wait);
            await edition.ChooseLocalCommand.Execute().ToTask();
            return prefetch.Running;
        });
        fetch.Release.SetResult();
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(running).IsTrue();
        await Assert.That(fetch.Interrupted.Task.IsCompleted).IsFalse();
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    [Arguments(false, true)]
    [Arguments(true, false)]
    public async Task The_wizard_leads_with_local_only_before_anything_is_installed(bool installed, bool expected) {
        var root = new LocalRoot(Config.PathTo("local"));
        if (installed) Config.CreateFile("local/install.json", "{}");
        var prefetch = new LocalBundlePrefetch(() => new FakeKcapCli(), CancellationToken.None);

        var preselected = await AvaloniaSession.DispatchAsync(async () => {
            using var harness = new WizardFixtures.GraphHarness(Config.Root) { LocalRoot = root };
            var graph = WizardComposition.BuildGraph(harness.Options() with { Prefetch = prefetch });
            await graph.ViewModel.PendingEnterForTesting;

            return graph.Steps.OfType<EditionStepViewModel>().Single().LocalPreselected;
        });
        await prefetch.Completion.WaitAsync(Wait);

        await Assert.That(preselected).IsEqualTo(expected);
        await Assert.That(prefetch.Started).IsEqualTo(expected);
    }

    /// The install reads the bundle the fetch is still downloading rather than download it a second time beside it,
    /// and the page shows that download as its own.
    [Test]
    public async Task An_install_waits_for_a_running_fetch_and_shows_its_download() {
        var fetch = new HeldFetch();
        var prefetchCli = fetch.Cli();
        var prefetch = new LocalBundlePrefetch(() => prefetchCli, CancellationToken.None);
        var setup = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(0, LocalSetupScript.Committed) };
        var controller = new LocalInstallController(
            () => setup, LocalSetupScript.NoSetup(Config), () => false, action => action(),
            TelemetryProbe.Live("setup", Config.Root).Funnel, prefetch);
        var settled = new TaskCompletionSource<LocalInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Settled += outcome => settled.TrySetResult(outcome);

        prefetch.Start();
        await fetch.Spawned.Task.WaitAsync(Wait);
        await controller.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
        fetch.Print!(new StreamedLine(ProcessStreamKind.Stdout, """{"v":1,"event":"downloading","bytes":100}"""));
        fetch.Print!(new StreamedLine(ProcessStreamKind.Stdout, """{"v":1,"event":"download_progress","received":40}"""));
        var during = (setup.SetupLocalCallCount, controller.Progress.Download.State, controller.Progress.DownloadFraction);

        fetch.Release.SetResult();
        var outcome = await settled.Task.WaitAsync(Wait);

        await Assert.That(during).IsEqualTo((0, RowState.Running, (double?)0.4));
        await Assert.That(setup.SetupLocalCallCount).IsEqualTo(1);
        await Assert.That(outcome).IsTypeOf<LocalInstallOutcome.Installed>();
    }

    /// Install pressed while the fetch still runs waits it out; a fetch that then finds no bundle settles the run without
    /// setup, which on Windows could otherwise reach the service's administrator prompt first. Elsewhere setup runs and
    /// reports the missing bundle itself.
    [Test]
    [Arguments(true, 0)]
    [Arguments(false, 1)]
    public async Task An_install_that_waited_on_a_fetch_finding_no_bundle_spawns_setup_only_off_windows(bool windows, int spawned) {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prefetchCli = FetchFailing("bundle_not_published", LocalBundlePrefetch.NotPublishedExit, release.Task);
        var prefetch = new LocalBundlePrefetch(() => prefetchCli, CancellationToken.None);
        var setup = new FakeKcapCli { SetupLocalBehavior = LocalSetupScript.Prints(LocalBundlePrefetch.NotPublishedExit, LocalSetupScript.Failed("bundle_not_published")) };
        var controller = new LocalInstallController(
            () => setup, LocalSetupScript.NoSetup(Config), () => false, action => action(),
            TelemetryProbe.Live("setup", Config.Root).Funnel, prefetch, settlesUnpublished: windows);
        var settled = new TaskCompletionSource<LocalInstallOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        controller.Settled += outcome => settled.TrySetResult(outcome);

        prefetch.Start();
        await controller.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
        release.SetResult();
        var outcome = await settled.Task.WaitAsync(Wait);

        await Assert.That(setup.SetupLocalCallCount).IsEqualTo(spawned);
        await Assert.That(((LocalInstallOutcome.Failed)outcome).Code).IsEqualTo("bundle_not_published");
    }

    [Test]
    public async Task Cancelling_an_install_that_waits_on_the_fetch_stops_both_without_spawning_setup() {
        var fetch = new HeldFetch();
        var prefetchCli = fetch.Cli();
        var prefetch = new LocalBundlePrefetch(() => prefetchCli, CancellationToken.None);
        var setup = new FakeKcapCli();
        var controller = new LocalInstallController(
            () => setup, LocalSetupScript.NoSetup(Config), () => false, action => action(),
            TelemetryProbe.Live("setup", Config.Root).Funnel, prefetch);

        prefetch.Start();
        await fetch.Spawned.Task.WaitAsync(Wait);
        await controller.StartAsync(LocalEmbeddingChoice.KeywordOnly, CancellationToken.None);
        await controller.SettleAsync().WaitAsync(Wait);

        await Assert.That(setup.SetupLocalCallCount).IsEqualTo(0);
        await Assert.That(fetch.Interrupted.Task.IsCompleted).IsTrue();
        await Assert.That(((LocalInstallOutcome.Failed)controller.Outcome!).Code).IsEqualTo("cancelled");
    }
}
