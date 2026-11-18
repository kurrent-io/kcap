using Capacitor.Cli.Recording.Capture;

namespace Capacitor.Cli.Commands;

/// <param name="LineNumber">The 0-based wire index the loss marker carries; the message prints it 1-based, as an editor counts.</param>
public sealed record CaptureLineLost(string SessionId, string? AgentId, int LineNumber, RedactionLossReason Reason)
    : ImportWarning(SessionId, AgentId) {
    public override string Message =>
        $"{Scope}line {LineNumber + 1} could not be captured: {CaptureLossMarker.ReasonName(Reason).Replace('_', ' ')}; loss marker queued";
}
