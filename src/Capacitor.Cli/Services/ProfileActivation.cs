using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Auth;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli.Services;

/// <summary>
/// <c>install --replace --verify --activate</c>: makes the profile the new unit is pinned to the active one,
/// in the same transaction that moves the daemon onto it. Each step returns its refusal token, or null.
/// </summary>
sealed class ProfileActivation(
        ConfigRoot config, string profile, string expectedServer, string serviceId,
        IReadOnlyDictionary<string, string> unitEnvironment, Func<string, Task> migrateLegacy) {
    public const string ReasonPrefix = "activation_reason=";

    public string Profile => profile;

    /// <summary>Whether the unit about to be written would serve the expected server under this name,
    /// resolved the way its daemon will resolve it.</summary>
    public string? Viability() {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var current)) return "activation_config_unreadable";

        var resolved = new ProfileResolver(current, cliServerUrl: null, unitEnvironment.GetValueOrDefault(ProfileOverrides.UrlVar),
            profile, repoConfig: null, repoRemoteUrls: [], repoPath: null).Resolve();
        if (!string.Equals(resolved.ProfileName, profile, StringComparison.Ordinal) || !ServerIdentity.SameServer(resolved.ServerUrl, expectedServer))
            return "expect_server_mismatch";

        return NamesService(resolved.Profile) ? null : "daemon_name_mismatch";
    }

    /// <summary>Settles the outgoing active profile's legacy credential while its daemon still runs, so the
    /// token lock's wait is spent before anything is stopped.</summary>
    public async Task<string?> PrepareAsync() {
        if (!ConfigMutator.TryLoadPure(AppConfig.GetConfigPath(config), out var current)) return "activation_config_unreadable";

        try {
            await migrateLegacy(current.ActiveName);
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Say($"activation: could not move the saved sign-in of profile '{current.ActiveName}': {ex.Message}");
            return "activation_migration_failed";
        }
        return null;
    }

    /// <summary>Sets <c>active_profile</c> after rechecking the target on the locked config. Never rolled back:
    /// the next start converges on whichever profile this leaves active.</summary>
    public async Task<string?> ActivateAsync() {
        try {
            await ConfigMutator.MutateStrictAsync(config, c =>
                c.Profiles.TryGetValue(profile, out var target) && ServerIdentity.SameServer(target.ServerUrl, expectedServer) && NamesService(target)
                    ? c with { ActiveProfile = profile }
                    : throw new StaleTargetException());
        } catch (StaleTargetException) {
            return "activation_stale";
        } catch (ConfigUnreadableException) {
            return "activation_config_unreadable";
        } catch (Exception ex) when (ex is not OperationCanceledException) {
            Say($"activation: {ex.Message}");
            return "activation_failed";
        }

        // The app reads this line as proof the selection moved; its absence proves nothing either way.
        try { Console.Out.WriteLine($"activated={profile}"); } catch (IOException) { }
        return null;
    }

    public static int Refusal(string reason) {
        Say($"{ReasonPrefix}{reason}");
        Say(VerifyExit.ActivationRefusedToken);
        return VerifyExit.ActivationRefused;
    }

    bool NamesService(Profile? target) =>
        DaemonStore.Sanitize(DaemonNameResolver.Resolve([], target?.Daemon?.Name)) == serviceId;

    static void Say(string line) {
        try { Console.Error.WriteLine(line); } catch (IOException) { }
    }

    sealed class StaleTargetException : Exception;
}
