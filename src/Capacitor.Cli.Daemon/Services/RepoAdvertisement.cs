using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>A repo-path list sent to the server and what it was built from: the <c>repos.json</c> read,
/// and the paths left out because they were not directories then. A reference type so a field holding
/// the last one sent can be swapped atomically from any thread.</summary>
internal sealed record RepoAdvertisement(RepoStoreFingerprint? Store, string[] Paths, string[] Missing) {
    public static readonly RepoAdvertisement None = new(null, [], []);

    /// <summary>Whether a send now would carry the same list. A repository created under a wildcard
    /// prefix is not looked for: finding one means walking the prefix, which registration does.</summary>
    public bool IsCurrent(RepoStoreFingerprint? store) =>
        store == Store && Paths.All(Directory.Exists) && !Missing.Any(Directory.Exists);
}
