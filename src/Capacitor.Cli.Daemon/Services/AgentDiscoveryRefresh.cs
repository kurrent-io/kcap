using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Refreshes the user's PATH and notices newly installed agents without reinstalling the service.</summary>
internal sealed class AgentDiscoveryRefresh(
        AgentPathEnvironment environment, AgentOrchestrator orchestrator,
        TimeProvider time, ILogger<AgentDiscoveryRefresh> logger) : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1), time);
        try {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false)) {
                try {
                    await environment.RefreshAsync(stoppingToken).ConfigureAwait(false);
                    orchestrator.RefreshAgentDiscovery();
                } catch (Exception ex) when (ex is not OperationCanceledException) {
                    logger.LogDebug(ex, "Agent environment refresh failed; keeping the last usable PATH");
                }
            }
        } catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}
