using Capacitor.Remote.Models;

namespace Capacitor.App.Services;

/// Messages is null when the read failed and the last list still stands.
public sealed record HeldMessagesRead(IReadOnlyList<ListedHeldMessageDto>? Messages) {
    public static readonly HeldMessagesRead Failed = new((IReadOnlyList<ListedHeldMessageDto>?)null);
    public static readonly HeldMessagesRead None = new([]);
}
