using System.Collections.Frozen;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels.NextWork;

/// <summary>The target keys the band showed on the previous visit, per server, so a row can say it is new.</summary>
public sealed class NextWorkSeenStore(IAppStateStore store, string serverUrl) {
    /// <summary>Kept bounded: a key that has left the feed for good would otherwise stay forever.</summary>
    const int MaxKeys = 500;

    string Key => serverUrl.TrimEnd('/');

    /// <summary>Null when this server has no previous visit, so no row is new.</summary>
    public async Task<IReadOnlySet<string>?> LoadAsync() {
        var state = await store.LoadAsync().ConfigureAwait(false);
        return state.NextWorkSeenByServer is { } seen && seen.TryGetValue(Key, out var keys)
            ? keys.ToFrozenSet(StringComparer.Ordinal)
            : null;
    }

    public Task SaveAsync(IEnumerable<string> keys) {
        var list = keys.Distinct(StringComparer.Ordinal).Take(MaxKeys).ToList();
        return store.UpdateAsync(state => state with {
            NextWorkSeenByServer = new Dictionary<string, IReadOnlyList<string>>(
                state.NextWorkSeenByServer ?? new Dictionary<string, IReadOnlyList<string>>(), StringComparer.Ordinal) { [Key] = list },
        });
    }
}
