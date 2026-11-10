using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Core;

/// <summary>
/// Whether a server may be sent a <see cref="LocalRepository"/> identity. An older server would store it as an
/// ordinary repository, link it to github.com and ask GitHub about it by folder name, so it is sent only to a
/// server whose last reported version is known to handle it; a server not yet heard from is not.
/// </summary>
public static class LocalRepositorySupport {
    // TODO(kcap-server#3023): lower to the first kcap-server release that ships local-repository support, once it is cut.
    // Until then no server qualifies, which keeps today's behaviour of sending no owner.
    public const string MinimumServerVersion = "1.5.0";

    /// <summary>An unknown or unparseable version sorts lowest, so it never accepts.</summary>
    public static bool Accepts(string? serverVersion) => PrereleaseSemver.Compare(serverVersion, MinimumServerVersion) >= 0;

    public static bool AcceptedBy(string? serverUrl, ConfigRoot config) => Accepts(ServerVersionStore.Get(serverUrl, config));

    /// <summary>
    /// Whether every server a session here could report to accepts one. Which profile a session reports to can
    /// depend on where it runs, and detection does not know, so every configured profile's server — and a server
    /// named by override — must. None configured is none known.
    /// </summary>
    public static async Task<bool> AcceptedEverywhereAsync(ConfigRoot config, ProfileOverrides overrides) {
        try {
            var (_, loaded) = await AppConfig.TryLoadProfileConfig(config);

            string[] servers = [
                .. loaded.Profiles.Values.Select(p => p.ServerUrl).OfType<string>().Where(u => u.Length > 0),
                .. overrides.Url is { } url ? [url] : Array.Empty<string>()
            ];

            return servers.Length > 0 && servers.All(server => AcceptedBy(server, config));
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            return false;
        }
    }

    /// <summary>The identity of the repository holding <paramref name="dir"/>, for a session reporting to
    /// <paramref name="serverUrl"/>; null when that server is not known to store one.</summary>
    public static (string Owner, string Name)? IdentityFor(ConfigRoot config, string? serverUrl, string dir) =>
        AcceptedBy(serverUrl, config) ? LocalRepository.Of(config, dir) : null;
}
