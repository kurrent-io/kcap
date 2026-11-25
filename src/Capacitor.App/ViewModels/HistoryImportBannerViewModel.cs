using System.Reactive;
using System.Reactive.Linq;
using Capacitor.App.ViewModels.Onboarding;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class HistoryImportBannerViewModel : ReactiveObject {
    public const string UnfinishedHeadline = "History import didn’t finish";
    public const string ReopenSetupLabel = "Reopen setup";
    public const string CancelImportLabel = "Cancel import";

    readonly HistoryImportRun _run;
    readonly bool _canOpenSetup;

    public HistoryImportBannerViewModel(HistoryImportRun run, Action? reopenSetup) {
        _run = run;
        _canOpenSetup = reopenSetup is not null;
        DismissCommand = ReactiveCommand.Create(run.DismissBanner);
        // Imported sessions stay imported and the server dedups a re-run, so cancelling needs no confirmation.
        CancelCommand = ReactiveCommand.CreateFromTask(run.CancelAsync, this.WhenAnyValue(x => x.CanCancel));
        ReopenSetupCommand = ReactiveCommand.Create(() => reopenSetup?.Invoke(),
            this.WhenAnyValue(x => x.ShowsReopenSetup));
    }

    public bool IsVisible => !_run.BannerDismissed;
    public bool Running => _run.Running;
    public bool CanCancel => _run.Running && !_run.CancelRequested;
    /// Hiding a running import must not read as stopping it.
    public string DismissLabel => Running ? "Hide" : "Dismiss";
    public string DismissAccessibleName => Running ? "Hide history import status" : "Dismiss history import status";
    /// The sole action once the import has finished; beside Cancel import or Reopen setup it is the quiet one.
    public bool DismissIsMain => !Running && !ShowsReopenSetup;
    public bool Succeeded => _run.State == ImportRunState.Finished;
    public bool NeedsAttention => _run.State is ImportRunState.Failed or ImportRunState.Cancelled;
    public bool ShowsReopenSetup => _canOpenSetup && _run.State is ImportRunState.Failed or ImportRunState.Cancelled;
    public string Headline => Running ? "Importing history"
        : _run.State is ImportRunState.Failed or ImportRunState.Cancelled ? UnfinishedHeadline
        : "History imported";
    public string Detail => Running || _run.State is ImportRunState.Failed or ImportRunState.Cancelled
        ? $"{_run.ProcessedOfExpected} {_run.SessionsNoun} processed"
        : $"{_run.Processed:N0} {(_run.Processed == 1 ? "session" : "sessions")} processed";
    public string Status => $"{Headline} · {Detail}";
    public double Progress => _run.Progress;
    public LucideIconKind Icon => Running ? LucideIconKind.LoaderCircle
        : _run.State is ImportRunState.Failed or ImportRunState.Cancelled ? LucideIconKind.CircleAlert
        : LucideIconKind.Check;
    public ReactiveCommand<Unit, Unit> DismissCommand { get; }
    public ReactiveCommand<Unit, Unit> CancelCommand { get; }
    public ReactiveCommand<Unit, Unit> ReopenSetupCommand { get; }

    internal IDisposable Watch() => _run.WhenAnyValue(x => x.State, x => x.Processed, x => x.PassesDone, x => x.BannerDismissed,
            x => x.CancelRequested,
            (state, processed, passes, dismissed, cancelling) => (state, processed, passes, dismissed, cancelling))
        .ObserveOn(RxSchedulers.MainThreadScheduler)
        .Subscribe(_ => {
            this.RaisePropertyChanged(nameof(IsVisible));
            this.RaisePropertyChanged(nameof(Running));
            this.RaisePropertyChanged(nameof(CanCancel));
            this.RaisePropertyChanged(nameof(DismissLabel));
            this.RaisePropertyChanged(nameof(DismissAccessibleName));
            this.RaisePropertyChanged(nameof(DismissIsMain));
            this.RaisePropertyChanged(nameof(Succeeded));
            this.RaisePropertyChanged(nameof(NeedsAttention));
            this.RaisePropertyChanged(nameof(ShowsReopenSetup));
            this.RaisePropertyChanged(nameof(Headline));
            this.RaisePropertyChanged(nameof(Detail));
            this.RaisePropertyChanged(nameof(Status));
            this.RaisePropertyChanged(nameof(Progress));
            this.RaisePropertyChanged(nameof(Icon));
        });
}
