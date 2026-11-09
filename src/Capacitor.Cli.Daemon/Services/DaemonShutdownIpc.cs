using System.Net.Sockets;
using Capacitor.Cli.Core.LocalIpc;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>
/// Local-socket handler for a client-requested shutdown. The intent is set before the exit begins so
/// the orchestrator's shutdown step stops or preserves agents accordingly. A relaunch goes through
/// the restart coordinator's forced path and the daemon's own restart strategy; a final stop never
/// waits on evals unless the request asks it to stop only when idle, and waits for a turn boundary only
/// when the request asks for one.
/// </summary>
internal sealed partial class DaemonShutdownIpc(
        AgentOrchestrator orchestrator, RestartCoordinator restart, IHostApplicationLifetime lifetime,
        ILogger<DaemonShutdownIpc> logger, StopCause? stopCause = null) {

    public async Task HandleAsync(string text, Stream stream, CancellationToken ct) {
        var request = ShutdownIpc.Parse(text);
        if (request is null) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error("shutdown: malformed request"), ct);

            return;
        }

        if (request.WhenIdle && restart.IsHardBusy()) {
            await FrameCodec.WriteAsync(stream, LocalFrame.Error("busy: an eval or a launch in admission is in progress"), ct);

            return;
        }

        var held = request is { AtTurnBoundary: true, Exit: "final" } && !restart.AtTurnBoundaryNow;
        if (held && !await WaitForTurnBoundaryAsync(stream, ct)) return;

        orchestrator.ShutdownIntent = request.Agents == "end" ? ShutdownIntent.EndAgents : ShutdownIntent.KeepAgents;
        LogShutdown(request.Agents, request.Exit);

        if (request.Exit == "final") {
            await WriteStoppingAsync(stream, held, ct);
            stopCause?.Note($"a local stop request (agents={request.Agents})");
            lifetime.StopApplication();

            return;
        }

        var reply = restart.RequestRestart(force: true) switch {
            RestartRequestResult.Restarting     => LocalFrame.ShutdownAck("stopping"),
            RestartRequestResult.Queued         => LocalFrame.ShutdownAck("queued"),
            RestartRequestResult.Failed         => LocalFrame.Error("shutdown: restart failed to start; the daemon will retry"),
            RestartRequestResult.ManualRequired => LocalFrame.Error("shutdown: foreground daemon — exit and restart it manually"),
            _                                   => LocalFrame.Error("shutdown: unknown restart result"),
        };
        await FrameCodec.WriteAsync(stream, reply, ct);
    }

    /// <summary>Once a held stop is released, a client that can no longer take the ack does not call it off.</summary>
    static async Task WriteStoppingAsync(Stream stream, bool held, CancellationToken ct) {
        try {
            await FrameCodec.WriteAsync(stream, LocalFrame.ShutdownAck("stopping"), ct);
        } catch (Exception ex) when (held && ex is IOException or SocketException or ObjectDisposedException) {
        }
    }

    /// <summary>Holds the stop on the restart coordinator's turn-boundary gate. False when the client hung up first,
    /// which withdraws the stop: nothing about the daemon has changed.</summary>
    async Task<bool> WaitForTurnBoundaryAsync(Stream stream, CancellationToken ct) {
        await FrameCodec.WriteAsync(stream, LocalFrame.ShutdownAck(ShutdownIpc.QueuedFor(restart.TurnWait)), ct);
        LogWaitingForTurns((int) restart.TurnWait.TotalSeconds);

        using var hangup = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var watch = WatchHangupAsync(stream, hangup);
        try {
            if (!await restart.WaitForTurnBoundaryAsync(hangup.Token)) LogTurnWaitRanOut();

            return true;
        } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
            LogStopWithdrawn();

            return false;
        } finally {
            await hangup.CancelAsync();
            await watch;
        }
    }

    /// <summary>The client sends nothing after its request, so a read completing at all means it has gone.</summary>
    static async Task WatchHangupAsync(Stream stream, CancellationTokenSource hangup) {
        try {
            if (await stream.ReadAsync(new byte[1], hangup.Token) > 0) {
                // Bytes after the request are outside the protocol; they end the hold like a hang-up.
            }
        } catch (OperationCanceledException) {
            return;
        } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) {
        }

        await hangup.CancelAsync();
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown requested: agents={Agents} exit={Exit}")]
    partial void LogShutdown(string agents, string exit);

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown held for a turn boundary, for at most {Seconds}s")]
    partial void LogWaitingForTurns(int seconds);

    [LoggerMessage(Level = LogLevel.Information, Message = "Turn wait ran out; stopping with turns still running")]
    partial void LogTurnWaitRanOut();

    [LoggerMessage(Level = LogLevel.Information, Message = "Shutdown withdrawn: the client closed the connection while it was held")]
    partial void LogStopWithdrawn();
}
