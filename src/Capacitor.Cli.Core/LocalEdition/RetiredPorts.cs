namespace Capacitor.Cli.Core.LocalEdition;

/// <summary>
/// The user-wide record of the installations a purge retired, <c>~/.config/kcap/local-retired.json</c>, in a registry's
/// shape with no active installation. A root's own registry is gone once the root is removed and is never read from
/// another <c>KCAP_LOCAL_ROOT</c>, so without this record a new installation could take a retired port, and a profile
/// still naming it would reach a different installation.
///
/// <para>Anchored at the home like <c>local-unit.lock</c>, never at <c>KCAP_LOCAL_ROOT</c> or <c>KCAP_CONFIG_DIR</c>, so
/// every root meets at one record. Writers hold the local unit lock, as setup does from its port choice through
/// install.</para>
/// </summary>
public static class RetiredPorts {
    public static string PathFor(UserHome home) => Path.Combine(home.Path, ".config", "kcap", "local-retired.json");

    /// <summary>A missing record reads as present and empty; an unreadable one is never read as empty, which would
    /// hand out a retired port.</summary>
    public static StateRead<InstallRegistry> Read(UserHome home) =>
        LocalStateFiles.ReadRegistryAt(PathFor(home)) switch {
            StateRead<InstallRegistry>.Absent => new StateRead<InstallRegistry>.Present(InstallRegistry.Empty),
            var read                          => read,
        };

    /// <summary>Adds <paramref name="retired"/> by an atomic replace, keeping every earlier entry; one already recorded
    /// is left as it is. Throws <see cref="LocalLifecycleException"/> rather than replace a record it cannot read.</summary>
    public static void Append(UserHome home, RetiredInstallation retired) {
        var path = PathFor(home);

        var registry = Read(home) switch {
            StateRead<InstallRegistry>.Present(var present) => present,
            StateRead<InstallRegistry>.Unreadable(var unreadable, var reason) =>
                throw new LocalLifecycleException($"The record of retired local server ports {unreadable} cannot be read ({reason})."),
            var other => throw new InvalidOperationException($"Unexpected read {other}."),
        };

        if (registry.Retired.Contains(retired)) return;

        DurableDirectory.Ensure(Path.GetDirectoryName(path)!, home.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        LocalStateFiles.PublishRegistryAt(path, new InstallRegistry(null, [.. registry.Retired, retired]));
    }
}
