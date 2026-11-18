using Capacitor.Cli.Daemon.Pty;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class NoopPtyProcessFactory : IPtyProcessFactory {
    public IPtyProcess Spawn(
            string command, string[] args, string cwd,
            Dictionary<string, string>? extraEnv = null, ushort cols = 120, ushort rows = 40,
            IReadOnlyCollection<string>? removeEnv = null
        ) => throw new NotSupportedException("never spawns a PTY");
}
