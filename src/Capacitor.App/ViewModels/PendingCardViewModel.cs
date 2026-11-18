using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Shared shell of a NEEDS YOU card. Setters are no-ops after disposal: the pipeline disposes a
/// card the instant its entry leaves the cache, and an in-flight submit continuation must not
/// notify a removed card.
public abstract class PendingCardViewModel : ReactiveObject, IDisposable {
    // CA1051: both fields are the shared shell every card subclass wires its own commands and
    // disposables into; a property indirection here buys nothing.
#pragma warning disable CA1051
    protected readonly CompositeDisposable Disposables = new();
    // canExecute feed; a BehaviorSubject rather than WhenAnyValue for the reason
    // SessionRailViewModel documents (headless ReactiveUI init ordering).
    protected readonly BehaviorSubject<bool> Busy = new(false);
#pragma warning restore CA1051
    readonly BehaviorSubject<bool> _answerable = new(true);
    bool _isBusy;
    string? _errorText;
    bool _canAnswer = true;
    string? _waitingOn;

    /// The cache key: lane-scoped, so two lanes' ids can never collide in a card list.
    public string Key { get; }
    public string RequestId { get; }
    internal DateTimeOffset RequestedAt { get; }
    protected bool IsDisposed { get; private set; }

    public bool IsBusy {
        get => _isBusy;
        protected set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            Busy.OnNext(value);
        }
    }

    public string? ErrorText {
        get => _errorText;
        protected set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _errorText, value);
        }
    }

    /// False for a viewer the server has not given the right to answer this kind of prompt: the card shows read-only.
    public bool CanAnswer {
        get => _canAnswer;
        private set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _canAnswer, value);
        }
    }

    /// Who a read-only card is waiting for; null while the viewer can answer it.
    public string? WaitingOn {
        get => _waitingOn;
        private set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _waitingOn, value);
        }
    }

    bool _offersAnswers = true;
    /// False where nobody here can answer: the card says where it waits and shows no answer controls.
    public bool OffersAnswers {
        get => _offersAnswers;
        private set {
            if (IsDisposed) return;
            this.RaiseAndSetIfChanged(ref _offersAnswers, value);
        }
    }

    /// Every answering command's canExecute: not in flight, and the viewer may answer.
    protected IObservable<bool> Ready { get; }

    protected PendingCardViewModel(PendingPermissionRequest entry, IObservable<AgentViewerAccess>? access) {
        Key = entry.Key;
        RequestId = entry.RequestId;
        RequestedAt = entry.RequestedAt;
        Disposables.Add(Busy);
        Ready = Busy.CombineLatest(_answerable, (busy, answerable) => !busy && answerable);
        if (access is not null)
            Disposables.Add(access.Subscribe(viewer => {
                if (IsDisposed) return;
                CanAnswer = viewer.CanAnswer(entry);
                WaitingOn = viewer.WaitingOn(entry);
                OffersAnswers = !viewer.AtTerminal;
                _answerable.OnNext(CanAnswer);
            }));
        Disposables.Add(_answerable);
    }

    public void Dispose() {
        IsDisposed = true;
        Disposables.Dispose();
        GC.SuppressFinalize(this);
    }

    /// The copy a failed answer shows; null for a conclusive outcome or one that needs no card text.
    protected static string? ErrorTextFor(PermissionResolveOutcome outcome) => outcome.Kind != PermissionResolveKind.TransportFailure ? null : outcome.Error switch {
        "daemon_unreachable" => "Daemon unreachable — try again",
        "server_unreachable" => "Server unreachable — try again",
        "not_signed_in" => "Sign in to answer",
        "withdraw_unsupported" => null,
        var reason => $"Could not answer ({reason}) — try again",
    };
}
