using Capacitor.App.Tests.Unit.Services;
using System.Reactive.Threading.Tasks;
using System.Windows.Input;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.ViewModels.Onboarding;
using Capacitor.Cli.Core;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class HistoryImportBannerViewModelTests {
    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(-1)]
    public async Task Dismissal_keeps_the_import_running_and_survives_its_result_and_reactivation(int exitCode) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var gate = new TaskCompletionSource<StreamingResult>();
            Action<StreamedLine>? emit = null;
            var run = new HistoryImportRun(2, 1, "recent history", 1, action => action());
            var banner = new HistoryImportBannerViewModel(run, () => { });
            using (banner.Watch()) {
                run.Start(new FakeKcapCli { ImportBehavior = (_, onLine, ct) => { emit = onLine; return gate.Task.WaitAsync(ct); } },
                    [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
                await banner.DismissCommand.Execute().ToTask();
                emit!(new StreamedLine(ProcessStreamKind.Stdout, "  Loading s1 (claude)"));
                await Assert.That(banner.IsVisible).IsFalse();
                await Assert.That(run.Running).IsTrue();
                await Assert.That(banner.Succeeded).IsFalse();
                await Assert.That(banner.NeedsAttention).IsFalse();
                await Assert.That(run.Processed).IsEqualTo(1);
            }

            if (exitCode < 0) await run.CancelAsync();
            else gate.SetResult(new StreamingResult(exitCode, false, []));
            await run.Completion;
            using var reactivation = banner.Watch();
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(banner.Running).IsFalse();
            await Assert.That(banner.Succeeded).IsEqualTo(exitCode == 0);
            await Assert.That(banner.NeedsAttention).IsEqualTo(exitCode != 0);
            await Assert.That(banner.ShowsReopenSetup).IsEqualTo(exitCode != 0);
            await Assert.That(new HistoryImportBannerViewModel(run, null).IsVisible).IsFalse();
            var fresh = new HistoryImportRun(2, 1, "recent history", 1, action => action());
            await Assert.That(new HistoryImportBannerViewModel(fresh, null).IsVisible).IsTrue();
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_unfinished_import_offers_the_supplied_setup_action(bool cancelled) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var run = new HistoryImportRun(1, 1, "recent history", 1, action => action());
            var reopened = 0;
            var banner = new HistoryImportBannerViewModel(run, () => reopened++);
            using var watch = banner.Watch();
            await Assert.That(((ICommand)banner.ReopenSetupCommand).CanExecute(null)).IsFalse();
            run.Start(new FakeKcapCli {
                ImportBehavior = async (_, _, ct) => {
                    if (cancelled) await Task.Delay(Timeout.InfiniteTimeSpan, ct);
                    return new StreamingResult(1, false, []);
                },
            }, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"])]);
            if (cancelled) await run.CancelAsync();
            else await run.Completion;

            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(banner.Headline).IsEqualTo("History import didn’t finish");
            await Assert.That(((ICommand)banner.ReopenSetupCommand).CanExecute(null)).IsTrue();
            await banner.ReopenSetupCommand.Execute().ToTask();
            await Assert.That(reopened).IsEqualTo(1);
            var withoutSetup = new HistoryImportBannerViewModel(run, null);
            await Assert.That(withoutSetup.ShowsReopenSetup).IsFalse();
            await Assert.That(((ICommand)withoutSetup.ReopenSetupCommand).CanExecute(null)).IsFalse();
        });
    }

    [Test]
    public async Task Cancel_import_stops_the_running_pass_once_and_lands_on_the_unfinished_state() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            var settled = new TaskCompletionSource();
            var passToken = CancellationToken.None;
            var imports = 0;
            var run = new HistoryImportRun(3, 1, "recent history", 2, action => action());
            var banner = new HistoryImportBannerViewModel(run, () => { });
            using var watch = banner.Watch();
            run.Start(new FakeKcapCli {
                ImportBehavior = async (_, onLine, ct) => {
                    imports++;
                    passToken = ct;
                    onLine(new StreamedLine(ProcessStreamKind.Stdout, "  Loading s1 (claude)"));
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException) { await settled.Task; throw; }
                    return new StreamingResult(0, false, []);
                },
            }, [new ImportRequest(ImportScopeChoice.Repo, null, [], ["a/b"]), new ImportRequest(ImportScopeChoice.Repo, null, [], ["c/d"])]);

            await Assert.That(banner.DismissLabel).IsEqualTo("Hide");
            await Assert.That(banner.DismissAccessibleName).IsEqualTo("Hide history import status");
            await Assert.That(((ICommand)banner.CancelCommand).CanExecute(null)).IsTrue();

            var cancelling = banner.CancelCommand.Execute().ToTask();
            await Assert.That(passToken.IsCancellationRequested).IsTrue();
            await Assert.That(run.Running).IsTrue();
            await Assert.That(((ICommand)banner.CancelCommand).CanExecute(null)).IsFalse();

            settled.SetResult();
            await cancelling;
            await Assert.That(imports).IsEqualTo(1);
            await Assert.That(run.State).IsEqualTo(ImportRunState.Cancelled);
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(banner.Status).IsEqualTo("History import didn’t finish · 1 of 3 sessions processed");
            await Assert.That(banner.ShowsReopenSetup).IsTrue();
            await Assert.That(banner.DismissLabel).IsEqualTo("Dismiss");
            await Assert.That(((ICommand)banner.CancelCommand).CanExecute(null)).IsFalse();
        });
    }
}
