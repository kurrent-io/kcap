using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// A work item page's read and the person's changes on it. A change answers whether the server recorded it.
public interface IWorkItemPageApi {
    /// Null when the read failed or the work item is gone.
    Task<WorkItemPartsDto?> ReadAsync(string workItemId, CancellationToken ct);
    /// A null status shows the worked-out one again.
    Task<bool> SetStatusAsync(string workItemId, string? status, CancellationToken ct);
    Task<bool> MakePartAsync(string workItemId, string looseEndId, CancellationToken ct);
    Task<bool> AddPartAsync(string workItemId, string title, CancellationToken ct);
    Task<bool> AddLinkAsync(string workItemId, string reference, CancellationToken ct);
}
