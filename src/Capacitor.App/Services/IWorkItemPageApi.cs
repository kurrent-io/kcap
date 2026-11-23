using System.Reactive;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A work item's read and a person's changes to it. A change answers whether the server recorded it.
public interface IWorkItemPageApi {
    /// Fires after every change the server recorded, so every surface showing work items reads again.
    IObservable<Unit> Changed { get; }
    /// The sessions whose last suggestion read found one still open, by canonical id.
    IObservable<IReadOnlySet<string>> SuggestedSessions { get; }
    /// Null when the read failed or the work item is gone.
    Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct);
    /// A null status shows the worked-out one again.
    Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct);
    Task<bool> RenameAsync(string workItemId, string title, CancellationToken ct);
    /// A null goal clears it.
    Task<bool> SetGoalAsync(string workItemId, string? goal, CancellationToken ct);
    /// <paramref name="partIds"/> names every slice the viewer can see, first to last.
    Task<bool> OrderPartsAsync(string workItemId, IReadOnlyList<string> partIds, CancellationToken ct);
    Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct);
    /// The new part's id, or null when the server refused it.
    Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct);
    /// Merges the part into its work item: its sessions and links move to the work item.
    Task<bool> RemovePartAsync(string workItemId, string partId, CancellationToken ct);
    /// Makes the part wait on another part of its work item, or stop waiting on it. Refused when they would wait on each other in a loop.
    Task<bool> SetWaitAsync(string partId, string waitsOnId, bool waits, CancellationToken ct);
    Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct);
    /// Creates a work item in the project with no session. Its id, or null when the server refused it.
    Task<string?> CreateAsync(string projectId, string title, CancellationToken ct);
    /// Creates a work item titled <paramref name="title"/> holding the session. Its id, or null when the server refused it.
    Task<string?> CreateForSessionAsync(string sessionId, string title, CancellationToken ct);
    /// Files the session under the part or work item, moving it from where it sits in that work item, else from its own.
    Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct);
    /// Takes the session out of the work item and every part of it, and kcap never adds it back.
    Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct);
    /// Null when kcap suggests nothing, the caller may not file the session, or the read failed.
    Task<WorkItemSuggestionDto?> ReadSuggestionAsync(string sessionId, CancellationToken ct);
    /// Puts the session on the suggested work item with no part.
    Task<bool> AcceptSuggestionAsync(string workItemId, string sessionId, CancellationToken ct);
    /// Leaves the session where it is and stops kcap suggesting that work item for it.
    Task<bool> DismissSuggestionAsync(string workItemId, string sessionId, CancellationToken ct);
}
