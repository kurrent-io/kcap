using System.Collections.Frozen;
using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

/// <summary>Steering for tests that start a control server: a switch over the daemon's own state directory that is
/// never started, so its socket binds only if a test turns the switch on.</summary>
static class TestSteering {
    public static readonly IReadOnlyDictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>> NoRoutes =
        FrozenDictionary<string, Func<SteeringHttpRequest, CancellationToken, Task<SteeringHttpResponse>>>.Empty;

    public static SteeringSwitch Switch(DaemonConfig config) =>
        new(new LaunchConsentStore(config.Store.StateDirectory(config.Name), NullLogger.Instance, TimeProvider.System), Endpoint(config));

    public static SteeringEndpoint Endpoint(DaemonConfig config) =>
        new(config.Store.SteeringSocketPath(config.Name), NoRoutes, TimeProvider.System, NullLogger.Instance);
}
