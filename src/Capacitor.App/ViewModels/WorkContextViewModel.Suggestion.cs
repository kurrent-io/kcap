using System.Reactive;
using System.Reactive.Disposables.Fluent;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The work item kcap suggests for a session in no work item, and the person's answer to it.
public sealed partial class WorkContextViewModel {
    WorkItemSuggestionDto? _suggestion;
    int _suggestionRead;

    public bool HasSuggestion => _suggestion is not null && Phase == WorkContextPhase.NoWorkItem;
    public string SuggestionTitle => _suggestion?.Title ?? "";
    public string? SuggestionReason => _suggestion?.Reason;
    bool _isAnsweringSuggestion;
    public bool IsAnsweringSuggestion { get => _isAnsweringSuggestion; private set => this.RaiseAndSetIfChanged(ref _isAnsweringSuggestion, value); }

    public ReactiveCommand<Unit, Unit> AcceptSuggestionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> DismissSuggestionCommand { get; private set; } = null!;

    void InitializeSuggestion() {
        var idle = this.WhenAnyValue(x => x.IsAnsweringSuggestion, busy => !busy);
        AcceptSuggestionCommand = ReactiveCommand.CreateFromTask(() => AnswerSuggestionAsync(
            (api, item, session) => api.AcceptSuggestionAsync(item, session, CancellationToken.None),
            "Couldn't add the session to the work item"), idle).DisposeWith(_disposables);
        DismissSuggestionCommand = ReactiveCommand.CreateFromTask(() => AnswerSuggestionAsync(
            (api, item, session) => api.DismissSuggestionAsync(item, session, CancellationToken.None),
            "Couldn't dismiss the suggestion"), idle).DisposeWith(_disposables);
    }

    async Task AnswerSuggestionAsync(Func<IWorkItemPageApi, string, string, Task<bool>> answer, string failure) {
        if (_workItems is not { } api || _suggestion is not { } suggestion || SessionIds.Canonical(_reader.CurrentState) is not { } session) return;
        IsAnsweringSuggestion = true;
        try {
            if (await answer(api, suggestion.WorkItemId, session)) SetSuggestion(null);
            else _notifier?.Notify(failure);
        } finally {
            IsAnsweringSuggestion = false;
        }
    }

    async void ReadSuggestion() {
        if (_workItems is not { } api || SessionIds.Canonical(_reader.CurrentState) is not { } session) return;
        var read = ++_suggestionRead;
        WorkItemSuggestionDto? suggestion;
        try {
            suggestion = await api.ReadSuggestionAsync(session, CancellationToken.None);
        } catch (Exception ex) {
            Console.Error.WriteLine($"kcap: suggestion read failed: {ex.Message}");
            return;
        }
        if (!_tornDown && read == _suggestionRead) SetSuggestion(suggestion);
    }

    void ClearSuggestion() {
        _suggestionRead++;
        SetSuggestion(null);
    }

    void SetSuggestion(WorkItemSuggestionDto? suggestion) {
        _suggestion = suggestion;
        RaiseSuggestion();
    }

    void RaiseSuggestion() {
        this.RaisePropertyChanged(nameof(HasSuggestion));
        this.RaisePropertyChanged(nameof(SuggestionTitle));
        this.RaisePropertyChanged(nameof(SuggestionReason));
    }
}
