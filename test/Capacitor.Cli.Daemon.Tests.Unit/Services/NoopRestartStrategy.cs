using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

sealed class NoopRestartStrategy : IRestartStrategy {
    public RestartOutcome Restart() => RestartOutcome.NoOp;
}
