using System.Runtime.CompilerServices;
using Capacitor.Cli.Daemon.Pty.Unix;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Unix;

/// <summary>Loads the daemon's code as this assembly loads, so its SIGCHLD read comes before any test can start a
/// process and have the runtime replace the disposition it reads, as the daemon's own startup does.</summary>
static class ChildReapersEarlyRead {
    [ModuleInitializer]
    internal static void Load() => _ = ChildReapers.RuntimeMayReap;
}
