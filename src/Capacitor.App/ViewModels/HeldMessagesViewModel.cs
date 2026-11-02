using System.Reactive.Disposables;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Avalonia.Collections;
using Capacitor.App.Services;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The messages held for an agent's owner: every one for the owner, only their own for anyone else. A server that sends
/// no viewer rights has nothing held, so nothing is read. Only the latest read is applied.
public sealed class HeldMessagesViewModel : ReactiveObject, IDisposable {
    readonly string _agentId;
    readonly IHeldMessagesApi _api;
    readonly CompositeDisposable _disposables = new();
    readonly CancellationTokenSource _lifetime = new();
    readonly AvaloniaList<HeldMessageItemViewModel> _items = new();
    AgentViewerAccess _viewer = AgentViewerAccess.Unrestricted;
    int _generation;
    bool _disposed;
    string? _errorText;

    public IAvaloniaReadOnlyList<HeldMessageItemViewModel> Items => _items;
    public bool HasItems => _items.Count > 0;

    public string? ErrorText {
        get => _errorText;
        private set => this.RaiseAndSetIfChanged(ref _errorText, value);
    }

    public HeldMessagesViewModel(string agentId, IHeldMessagesApi api, IObservable<string> changed, IObservable<AgentViewerAccess> viewer) {
        _agentId = agentId;
        _api = api;
        // A list read under other rights is never shown under these: an owner's list carries every
        // sender's messages and their Approve and Reject.
        viewer.DistinctUntilChanged().ObserveOn(RxSchedulers.MainThreadScheduler).Subscribe(v => {
            _viewer = v;
            _generation++;
            ErrorText = null;
            Replace([]);
            Refresh();
        }).DisposeWith(_disposables);
        changed.Where(id => id == agentId).ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => Refresh()).DisposeWith(_disposables);
    }

    public void Refresh() {
        if (_disposed || !_viewer.HasRights) return;
        _ = LoadAsync(++_generation);
    }

    async Task LoadAsync(int generation) {
        HeldMessagesRead read;
        try {
            read = await _api.ListAsync(_agentId, _lifetime.Token);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            return;
        }
        if (_disposed || generation != _generation || read.Messages is not { } messages) return;
        var owner = _viewer.IsOwner;
        Replace(messages.Select(m => new HeldMessageItemViewModel(m, owner, DecideAsync)).ToList());
    }

    async Task DecideAsync(HeldMessageItemViewModel item, HeldMessageDecision decision) {
        if (_disposed) return;
        item.IsBusy = true;
        ErrorText = null;
        HeldMessageDecisionOutcome outcome;
        try {
            outcome = await _api.DecideAsync(_agentId, item.DispatchId, decision, _lifetime.Token);
        } catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) {
            return;
        }
        if (_disposed) return;
        item.IsBusy = false;
        ErrorText = outcome.Refusal;
        Refresh();
    }

    void Replace(IReadOnlyList<HeldMessageItemViewModel> next) {
        var old = _items.ToList();
        _items.Clear();
        _items.AddRange(next);
        foreach (var item in old) item.Dispose();
        this.RaisePropertyChanged(nameof(HasItems));
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        _disposables.Dispose();
        try { _lifetime.Cancel(); } catch (ObjectDisposedException) { }
        _lifetime.Dispose();
        foreach (var item in _items) item.Dispose();
    }
}
