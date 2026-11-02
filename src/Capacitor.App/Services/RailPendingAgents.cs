namespace Capacitor.App.Services;

/// The agents the rail marks as needing you. A session with established access is left to its
/// cards: the feed reconciles it into the cache its chat reads, while the tracker also lists
/// interrupts no card renders, so counting both lit the row with nothing to answer.
public static class RailPendingAgents {
    public static IReadOnlySet<string> Compose(
            IReadOnlySet<string> cards, IReadOnlySet<string> attention, IReadOnlySet<string> open,
            IReadOnlyDictionary<string, string> sessionAgents) =>
        cards
            .Concat(attention.Where(s => !open.Contains(s))
                .Select(s => sessionAgents.GetValueOrDefault(s, "")).Where(a => a.Length > 0))
            .ToHashSet(StringComparer.Ordinal);
}
