using Capacitor.Cli.Core;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

internal partial class AgentOrchestrator {
    /// <summary>How long a terminal compaction, whose end the daemon cannot tell, holds off the next one once typed: a
    /// large Claude context takes a minute or two to compact, and the page waits as long for a reading.</summary>
    internal static readonly TimeSpan CompactionHold = TimeSpan.FromMinutes(2);

    /// <summary>A server's request to compact an agent's context, answered like a dispatched input: acknowledged once
    /// the runtime has it, or rejected with the reason. It is refused while a question, permission prompt or terminal
    /// dialog is open, and dropped while an earlier compaction is still queued or running, so two viewers asking at
    /// once compact once.</summary>
    async Task HandleCompactAgent(CompactAgentCommand cmd) {
        var input = new SendInputCommand(cmd.AgentId, "/compact", null, cmd.DispatchId);

        if (!await AdmitDispatchAsync(input)) return;

        try {
            if (!_agents.TryGetValue(cmd.AgentId, out var agent)) {
                await ReportInputDroppedAsync(input, SendInputDropReason.UnknownAgent);
                return;
            }

            if (agent.IsPrivate) {
                await ReportInputDroppedAsync(input, SendInputDropReason.PrivateAgent);
                return;
            }

            if (StartCompaction(agent, cmd.DispatchId, out var written) is { } refusal) {
                LogCompactionRefused(agent.Id, agent.Runtime.Vendor, refusal);
                await ReportInputDroppedAsync(input, refusal);
                return;
            }

            LogCompactionStarted(agent.Id, agent.Runtime.Vendor);
            AcknowledgeInput(input);
            if (written is { IsCompletedSuccessfully: false }) _ = ReportLostWriteAsync(input, agent.Runtime.Vendor, written);
        } catch {
            _dispatchedInputs.Forget(cmd.DispatchId);

            throw;
        }
    }

    /// <summary>Starts the compaction, or returns the drop reason it was refused for.</summary>
    string? StartCompaction(AgentInstance agent, Guid dispatchId, out Task? written) {
        written = null;

        if (!AcceptsTypedInput(agent.Kind)) return SendInputDropReason.CompactionUnsupported;
        if (IsClosingToInput(agent)) return SendInputDropReason.DeliveryFailed;
        if (agent.TerminalDialog is not null || _permissionBroker.HasPendingFor(agent.Id)) return SendInputDropReason.PromptOpen;

        lock (agent.CompactionGate) {
            if (agent.Compaction is { } running && !running.IsOver(CompactionHold)) return SendInputDropReason.CompactionRunning;

            CompactionStart start;
            try {
                start = agent.Runtime.StartCompaction(dispatchId);
            } catch (InputNotAdmittedException) {
                return SendInputDropReason.DeliveryFailed;
            }

            switch (start.Kind) {
                case CompactionStartKind.PromptOpen:  return SendInputDropReason.PromptOpen;
                case CompactionStartKind.Unsupported: return SendInputDropReason.CompactionUnsupported;
            }

            agent.Compaction = new RunningCompaction(start, _time);
            written          = start.Written;
        }

        agent.ActivityClock.Advance();

        return null;
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Compaction started for agent {AgentId} (vendor={Vendor})")]
    partial void LogCompactionStarted(string agentId, string vendor);

    [LoggerMessage(Level = LogLevel.Information, Message = "Compaction refused for agent {AgentId} (vendor={Vendor}): {Reason}")]
    partial void LogCompactionRefused(string agentId, string vendor, string reason);
}
