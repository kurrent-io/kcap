using System.Reactive;
using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A work item's read and a person's changes to it. A change answers whether the server recorded it.
public interface IWorkItemPageApi {
    /// Fires after every change the server recorded, so every surface showing work items reads again.
    IObservable<Unit> Changed { get; }
    /// Null when the read failed or the work item is gone.
    Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct);
    /// A null status shows the worked-out one again.
    Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct);
    Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct);
    /// The new part's id, or null when the server refused it.
    Task<string?> AddPartAsync(string workItemId, string title, CancellationToken ct);
    Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct);
    /// Moves the session to the part, or onto the work item itself, from where it sits in the same work item.
    Task<bool> MoveSessionAsync(string workItemId, string sessionId, CancellationToken ct);
    /// Takes the session out of the work item and every part of it, and kcap never adds it back.
    Task<bool> RemoveSessionAsync(string workItemId, string sessionId, CancellationToken ct);
}
