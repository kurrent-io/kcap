using ReactiveUnit = System.Reactive.Unit;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

/// Records every change and answers it with <see cref="Records"/>; a recorded change fires <see cref="Changed"/>.
sealed class FakeWorkItemPageApi : IWorkItemPageApi {
    readonly Subject<ReactiveUnit> _changed = new();
    public IObservable<ReactiveUnit> Changed => _changed;
    public IObservable<IReadOnlySet<string>> SuggestedSessions { get; } = new BehaviorSubject<IReadOnlySet<string>>(new HashSet<string>());
    /// What the suggestion read answers; a recorded accept or dismiss clears it, as the server stops suggesting.
    public WorkItemSuggestionDto? Suggestion { get; set; }
    public List<(string Answer, string WorkItemId, string SessionId)> SuggestionAnswers { get; } = [];
    public WorkItemPartsDto? Read { get; set; }
    public bool Records { get; set; } = true;
    public List<(string Id, string? Status)> StatusesSet { get; } = [];
    public List<(string WorkItemId, string Title)> PartsAdded { get; } = [];
    public List<(string WorkItemId, string SessionId)> Moves { get; } = [];
    public List<(string WorkItemId, string SessionId)> Removals { get; } = [];

    public Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) => Task.FromResult(Read);

    public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) {
        StatusesSet.Add((workItemId, status));
        return Answer();
    }

    public Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct) {
        if (Read is { } read) {
            var looseEnd = read.LooseEnds.Single(l => l.LooseEndId == looseEndId);
            Read = read with {
                LooseEnds = [.. read.LooseEnds.Where(l => l != looseEnd)],
                Parts = [.. read.Parts, new() { WorkItemId = looseEndId, Title = looseEnd.Text, Status = "open" }],
            };
        }
        return Answer();
    }

    public async Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct) {
        PartsAdded.Add((workItemId, title));
        return await Answer() ? $"part-{PartsAdded.Count}" : null;
    }

    public Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct) => Answer();

    public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) {
        Moves.Add((workItemId, sessionId));
        return Answer();
    }

    public Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) {
        Removals.Add((workItemId, sessionId));
        return Answer();
    }

    public Task<WorkItemSuggestionDto?> ReadSuggestionAsync(string sessionId, CancellationToken ct) => Task.FromResult(Suggestion);

    public Task<bool> AcceptSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) => AnswerSuggestion("accept", workItemId, sessionId);

    public Task<bool> DismissSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) => AnswerSuggestion("dismiss", workItemId, sessionId);

    Task<bool> AnswerSuggestion(string answer, string workItemId, string sessionId) {
        SuggestionAnswers.Add((answer, workItemId, sessionId));
        if (Records) Suggestion = null;
        return Answer();
    }

    Task<bool> Answer() {
        if (Records) _changed.OnNext(ReactiveUnit.Default);
        return Task.FromResult(Records);
    }
}
