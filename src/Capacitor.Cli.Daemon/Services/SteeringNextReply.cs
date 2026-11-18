namespace Capacitor.Cli.Daemon.Services;

/// <summary>The answer to <c>POST /next</c> when there is one: <c>{dispatchId, text}</c>, a dispatch the server confirmed
/// is now the mod's, or <c>{gone, retry}</c> for an agent id this daemon holds no attachment under, retry meaning
/// attach again.</summary>
internal sealed record SteeringNextReply(string? DispatchId = null, string? Text = null, string? Gone = null, bool? Retry = null) {
    public static SteeringNextReply Handover(Guid dispatchId, string text) => new(dispatchId.ToString("D"), text);

    public static SteeringNextReply GoneAs(string reason, bool retry) => new(Gone: reason, Retry: retry);
}
