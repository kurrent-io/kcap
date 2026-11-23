namespace Capacitor.App.ViewModels.Pages;

/// What each slice of a work item waits on, by id.
public sealed class WorkItemPageWaits(IReadOnlyDictionary<string, HashSet<string>> waits) {
    public static WorkItemPageWaits None { get; } = new(new Dictionary<string, HashSet<string>>());

    public bool Waits(string id, string on) => waits.TryGetValue(id, out var waited) && waited.Contains(on);

    /// Whether <paramref name="from"/> waits on <paramref name="target"/>, directly or through others, so that
    /// <paramref name="target"/> waiting on it would make a loop.
    public bool Reaches(string from, string target) {
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var pending = new Stack<string>([from]);
        while (pending.TryPop(out var id)) {
            if (id == target) return true;
            if (!waits.TryGetValue(id, out var next)) continue;
            foreach (var on in next)
                if (seen.Add(on)) pending.Push(on);
        }
        return false;
    }
}
