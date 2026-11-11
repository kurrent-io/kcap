using System.Runtime.InteropServices;
using Capacitor.Cli.Core.Http;
using Capacitor.Cli.Core.Install;
using Capacitor.Cli.Core.LocalEdition;

namespace Capacitor.App.Services.LocalSetup;

/// The built-in model set the version setup will run offers, looked up as the bundled CLI looks it up. Any failure
/// reads as none: setup then decides without it, and the choice does not offer it.
public static class LocalBuiltinOffer {
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    // One client for the app's lifetime, so its kept manifests answer a version that offers no built-in model too.
    static readonly LocalBundleClient Bundles = new(Http, TimeProvider.System);

    static readonly LocalBuiltinOfferCache Lookups = new(version =>
        new LocalEmbeddingOffer(Bundles, LocalBundleManifestKey.Load(), RuntimeInformation.RuntimeIdentifier).LookupAsync(version, CancellationToken.None));

    public static async Task<LocalEmbeddingModelManifest?> LookupAsync(LocalRoot root, CancellationToken ct) {
        var version = await Task.Run(() => TargetVersion(root), ct);

        return ReleaseManifest.IsValidVersion(version) ? await Lookups.LookupAsync(version, ct) : null;
    }

    /// The installed bundle pins the required model set. The recorded set can belong to an older version.
    public static bool InstalledModelReady(LocalRoot root) =>
        LocalEndpoints.Read(root).Active?.Version is { } version && ReleaseManifest.IsValidVersion(version)
        && LocalModelSets.Bundled(root, version) is { } model && LocalModelSets.IsPresent(root, model);

    /// The installed version on a re-run, else the one the bundled CLI installs, which shares this app's build.
    public static string TargetVersion(LocalRoot root) => LocalEndpoints.Read(root).Active?.Version ?? LocalBundleClient.RequestedVersion();
}
