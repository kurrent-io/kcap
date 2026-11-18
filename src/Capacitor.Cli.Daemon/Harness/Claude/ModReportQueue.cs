using Capacitor.Cli.Daemon.Services;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>
/// What became of the dispatches the mod was handed, owed to the server until it answers. Sent oldest first. One that may
/// not have arrived holds back the rest, which would go on the same connection. One the server answered with an error is
/// sent again by each later sweep, holding back only what is reported about the same dispatch, until it has been refused
/// <see cref="MaxRefusals"/> times, since an error can be the server not yet knowing this connection as the agent's.
///
/// <para>A queued report is the mod's record of its dispatch once the mod has handed it over, so while one is here a
/// dispatch must never be reported lost.</para>
/// </summary>
internal sealed partial class ModReportQueue(ServerConnection server, ILogger logger) {
    const int Cap = 1024;

    /// <summary>Refused this many times, a report is dropped: three minutes of sweeps.</summary>
    internal const int MaxRefusals = 36;

    readonly Lock                 _gate     = new();
    readonly List<ModInputReport> _reports  = [];
    readonly KeyedAsyncGate       _flushing = new();

    /// <summary>The dispatches whose reports wait for the next sweep, behind one the server refused.</summary>
    readonly HashSet<Guid> _waitingForSweep = [];

    /// <summary>Runs once the last report about a dispatch has gone.</summary>
    public Action<Guid>? Settled { get; set; }

    /// <summary>Runs after a flush changes the queue, outside the queue's lock. An enqueue does not run it: its caller
    /// saves, and may hold its own lock while enqueuing.</summary>
    public Action? Changed { get; set; }

    /// <summary>Whether a report about the dispatch must stay past the cap: the mod still has it, and a report evicted
    /// would leave it to read as lost.</summary>
    public Func<Guid, bool>? Keeps { get; set; }

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
    /// reached it produces. Past the cap the oldest goes that <see cref="Keeps"/> doesn't keep; those it keeps are bounded
    /// by what the server handed over, so only they take the queue past the cap.</summary>
    public void Enqueue(ModInputReport report) {
        ModInputReport[] queued;

        lock (_gate) {
            if (_reports.Any(r => r.SaysTheSameAs(report))) return;

            _reports.Add(report);

            if (_reports.Count <= Cap) return;

            queued = [.. _reports];
        }

        // Asked outside this lock: whoever answers it takes its own, which may be held around a call into this.
        if (queued.FirstOrDefault(r => Keeps?.Invoke(r.DispatchId) != true) is not { } oldest) return;

        lock (_gate) {
            if (_reports.Count <= Cap || !_reports.Remove(oldest)) return;
        }

        LogDropped(oldest.DispatchId, oldest.Kind);
    }

    /// <param name="sweep">Whether this is a sweep's flush, the one that sends again what the server refused.</param>
    public async Task FlushAsync(bool sweep = false) {
        using var flushing = await _flushing.EnterAsync(nameof(FlushAsync), CancellationToken.None);

        if (sweep) lock (_gate) _waitingForSweep.Clear();

        while (server.IsReady) {
            ModInputReport? next;
            lock (_gate) next = _reports.FirstOrDefault(r => !_waitingForSweep.Contains(r.DispatchId));

            if (next is null) return;

            var answer = await SendAsync(next);
            if (answer is AttachedReportAnswer.Unreached) return;

            var again = answer is AttachedReportAnswer.Refused && next.Refusals + 1 < MaxRefusals;
            bool settled;

            lock (_gate) {
                var at = _reports.IndexOf(next);

                if (again) {
                    if (at >= 0) _reports[at] = next with { Refusals = next.Refusals + 1 };
                    _waitingForSweep.Add(next.DispatchId);
                } else if (at >= 0) {
                    _reports.RemoveAt(at);
                }

                settled = !again && _reports.All(r => r.DispatchId != next.DispatchId);
            }

            switch (answer) {
                case AttachedReportAnswer.Refused when again: LogRefusedAgain(next.Kind, next.DispatchId, next.AgentId, next.Refusals + 1, MaxRefusals); break;
                case AttachedReportAnswer.Refused:            LogRefusedDropped(next.Kind, next.DispatchId, next.AgentId, MaxRefusals); break;
                case AttachedReportAnswer.NotRecorded:        LogNotRecorded(next.Kind, next.DispatchId, next.AgentId); break;
            }

            if (settled) Settled?.Invoke(next.DispatchId);
            Changed?.Invoke();
        }
    }

    Task<AttachedReportAnswer> SendAsync(ModInputReport report) => report.Kind switch {
        ModInputReport.Link        => server.ReportInputLinkAsync(new(report.DispatchId, report.AgentId, report.SessionId!, report.RowUuid!.Value)),
        ModInputReport.Unconfirmed => server.ReportInputUnconfirmedAsync(new(report.DispatchId, report.AgentId, report.Reason!)),
        _                          => server.ReportAttachedInputRejectedAsync(report.DispatchId, report.AgentId, report.Reason!),
    };

    [LoggerMessage(Level = LogLevel.Warning, Message = "The steering report queue is full; dropped the {Kind} report of dispatch {DispatchId}")]
    partial void LogDropped(Guid dispatchId, string kind);

    [LoggerMessage(Level = LogLevel.Information, Message = "The server refused the {Kind} report of dispatch {DispatchId} under agent {AgentId} ({Refusals} of {Max}); the next sweep sends it again")]
    partial void LogRefusedAgain(string kind, Guid dispatchId, string agentId, int refusals, int max);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server refused the {Kind} report of dispatch {DispatchId} under agent {AgentId} {Refusals} times; it was dropped")]
    partial void LogRefusedDropped(string kind, Guid dispatchId, string agentId, int refusals);

    [LoggerMessage(Level = LogLevel.Warning, Message = "The server holds no dispatch {DispatchId} for agent {AgentId}, so its {Kind} report was dropped")]
    partial void LogNotRecorded(string kind, Guid dispatchId, string agentId);
}
