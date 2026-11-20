using System.Diagnostics;
using Capacitor.Cli.Core.Config;

namespace Capacitor.Cli;

/// <summary>Points a kcap this process spawns at the server and credential it resolved itself.</summary>
public static class ChildProfile {
    /// <summary>A resolved profile travels by name, never as its URL: a URL outranks every profile in the child,
    /// which then presents the active profile's token, and another profile's server refuses it.</summary>
    public static void Apply(ProcessStartInfo psi, ProfileContext profiles) {
        if (profiles.Resolution.ProfileName is { Length: > 0 } name) {
            psi.Environment[ProfileOverrides.ProfileVar] = name;
            psi.Environment.Remove(ProfileOverrides.UrlVar);
        } else {
            psi.Environment[ProfileOverrides.UrlVar] = profiles.Resolution.ServerUrl;
        }
    }
}
