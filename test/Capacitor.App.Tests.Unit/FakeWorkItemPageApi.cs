using System.Collections.Frozen;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit;

public sealed class FakeWorkItemPageApi : IWorkItemPageApi {
    public List<string> Calls { get; } = [];
    public bool Accepts { get; set; } = true;
    public IObservable<System.Reactive.Unit> Changed => Observable.Never<System.Reactive.Unit>();
    public IObservable<IReadOnlySet<string>> SuggestedSessions => Observable.Return<IReadOnlySet<string>>(FrozenSet<string>.Empty);
    public List<WorkItemPartDetailDto> Parts { get; init; } = [Part("p1", "in_progress", "s1")];
    public List<string> SessionIds { get; init; } = [];

    public static WorkItemPartDetailDto Part(string id, string status, params string[] sessions) => new() {
        WorkItemId = id, Title = id == "p1" ? "Card form" : id, Status = status, SessionIds = [.. sessions],
        Sessions = [.. sessions.Select(s => new WorkItemPartSessionDto { SessionId = s, Title = "Spike the card form", Vendor = "codex" })],
    };

    public Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct) => Task.FromResult<WorkItemPartsDto?>(
        new() { WorkItem = new() { WorkItemId = workItemId, Title = "Checkout" }, Goal = "Pay in two steps", Parts = Parts, SessionIds = SessionIds });
    public Task<bool> RenameAsync(string workItemId, string title, CancellationToken ct) => Record($"title {title}");
    public Task<bool> SetGoalAsync(string workItemId, string? goal, CancellationToken ct) => Record($"goal {goal ?? "<none>"}");
    public Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct) => Record($"status {workItemId} {status ?? "<links>"}");
    public Task<bool> OrderPartsAsync(string workItemId, IReadOnlyList<string> partIds, CancellationToken ct) => Record($"order {string.Join(",", partIds)}");
    public Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) => Record($"move {sessionId} to {workItemId}");
    public Task<bool> SetWaitAsync(string partId, string waitsOnId, bool waits, CancellationToken ct) =>
        Record($"{(waits ? "wait" : "stop waiting")} {partId} on {waitsOnId}");

    public async Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct) =>
        await Record($"add {title}") ? "p9" : null;

    Task<bool> Record(string call) {
        Calls.Add(call);
        return Task.FromResult(Accepts);
    }

    public Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> CreateAsync(string projectId, string title, CancellationToken ct) => throw new NotSupportedException();
    public Task<string?> CreateForSessionAsync(string sessionId, string title, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> RemovePartAsync(string workItemId, string partId, CancellationToken ct) => throw new NotSupportedException();
    public Task<WorkItemSuggestionDto?> ReadSuggestionAsync(string sessionId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> AcceptSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
    public Task<bool> DismissSuggestionAsync(string workItemId, string sessionId, CancellationToken ct) => throw new NotSupportedException();
}
