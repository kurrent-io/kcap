using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// What became of the dispatches the mod was handed, owed to the server until it answers. Sent oldest first. One that may
/// not have arrived holds back the rest, which would go on the same connection; one the server answered with an error is
/// dropped and logged, since sending it again would only repeat that answer.
///
/// <para>A queued report is the mod's record of its dispatch once the mod has handed it over, so while one is here a
/// dispatch must never be reported lost.</para>
/// </summary>
internal sealed partial class ModReportQueue(ServerConnection server, ILogger logger) {
    const int Cap = 1024;

    readonly Lock                 _gate     = new();
    readonly List<ModInputReport> _reports  = [];
    readonly KeyedAsyncGate       _flushing = new();

    /// <summary>Runs once the last report about a dispatch has gone.</summary>
    public Action<Guid>? Settled { get; set; }

    /// <summary>Runs after a flush changes the queue, outside the queue's lock. An enqueue does not run it: its caller
    /// saves, and may hold its own lock while enqueuing.</summary>
    public Action? Changed { get; set; }

    public void Restore(IEnumerable<ModInputReport> reports) {
        lock (_gate) _reports.AddRange(reports);
    }

    public ModInputReport[] Snapshot() {
        lock (_gate) return [.. _reports];
    }

    public bool Holds(Guid dispatchId) {
        lock (_gate) return _reports.Any(r => r.DispatchId == dispatchId);
    }

    /// <summary>Queues a report unless the same one is queued already, which a mod resending it before an answer
    /// reached it produces.</summary>
    public void Enqueue(ModInputReport report) {
        lock (_gate) {
            if (_reports.Contains(report)) return;

            if (_reports.Count >= Cap) {
                LogDropped(_reports[0].DispatchId, _reports[0].Kind);
                _reports.RemoveAt(0);
            }

            _reports.Add(report);
        }
    }

    public async Task FlushAsync() {
        using var flushing = await _flushing.EnterAsync(nameof(FlushAsync), CancellationToken.None);

        while (server.IsReady) {
            ModInputReport? next;
            lock (_gate) next = _reports.FirstOrDefault();

            if (next is null) return;

            var answer = await SendAsync(next);
            if (answer is null) return;
            if (answer == false) LogRefused(next.Kind, next.DispatchId, next.AgentId);

            bool settled;

            lock (_gate) {
                _reports.Remove(next);
                settled = _reports.All(r => r.DispatchId != next.DispatchId);
            }

            if (settled) Settled?.Invoke(next.DispatchId);
            Changed?.Invoke();
        }
    }

    Task<bool?> SendAsync(ModInputReport report) => report.Kind switch {
        ModInputReport.Link        => server.ReportInputLinkAsync(new(report.DispatchId, report.AgentId, report.SessionId!, report.RowUuid!.Value)),
        ModInputReport.Unconfirmed => server.ReportInputUnconfirmedAsync(new(report.DispatchId, report.AgentId, report.Reason!)),
        _                          => server.ReportAttachedInputRejectedAsync(report.DispatchId, report.AgentId, report.Reason!),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The steering report queue is full; dropped the {Kind} report of dispatch {DispatchId}")]
    partial void LogDropped(Guid dispatchId, string kind);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused the {Kind} report of dispatch {DispatchId} under agent {AgentId}; it was dropped")]
    partial void LogRefused(string kind, Guid dispatchId, string agentId);
}
