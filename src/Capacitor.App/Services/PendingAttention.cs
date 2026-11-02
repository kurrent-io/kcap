namespace Capacitor.App.Services;

/// What the rail and tray mark as waiting. A session with established access is left to its
/// cards: the feed reconciles it into the cache its chat reads, while the tracker also lists
/// interrupts no card renders, so counting both marked it with nothing to answer.
public static class PendingAttention {
    public static IReadOnlySet<string> Unopened(IReadOnlySet<string> attention, IReadOnlySet<string> open) =>
        attention.Where(s => !open.Contains(s)).ToHashSet(StringComparer.Ordinal);

    /// The tray lists a session for either reason: still unopened, or holding a card.
    public static IReadOnlySet<string> TraySessions(IReadOnlySet<string> unopened, IEnumerable<PendingPermissionRequest> cards) =>
        unopened.Concat(cards.Select(p => p.SessionId).Where(s => s.Length > 0)).ToHashSet(StringComparer.Ordinal);

    public static IReadOnlySet<string> Agents(
            IReadOnlySet<string> cards, IReadOnlySet<string> unopened, IReadOnlyDictionary<string, string> sessionAgents) =>
        cards
            .Concat(unopened.Select(s => sessionAgents.GetValueOrDefault(s, "")).Where(a => a.Length > 0))
            .ToHashSet(StringComparer.Ordinal);
}
