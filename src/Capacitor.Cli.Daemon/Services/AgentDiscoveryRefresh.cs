using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Polls agent discovery and refreshes the terminal PATH where supported.</summary>
internal sealed class AgentDiscoveryRefresh(
        AgentPathEnvironment? environment, AgentOrchestrator orchestrator,
        TimeProvider time, ILogger<AgentDiscoveryRefresh> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                try {
                    if (environment is not null) await environment.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    orchestrator.RefreshAgentDiscovery();
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    logger.LogDebug(ex, "Agent discovery refresh failed");
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
