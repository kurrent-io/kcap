using Capacitor.Remote.Models;

namespace Capacitor.Client;

/// The server's whole queue for one session, as of one push or one chat join.
public sealed record PendingInputUpdate(string SessionId, IReadOnlyList<QueuedInputItem> Items);
