using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core;

/// <summary>The server's answer to <c>AgentRegisteredAcked</c>. Not registered means the server holds no live
/// entry under the agent id. <see cref="RefusedReason"/> is an open set; a value this daemon does not know
/// is still a refusal.</summary>
public sealed record AgentRegisteredResponse(bool Registered, string? RefusedReason = null) {
    public static readonly AgentRegisteredResponse Accepted = new(true);

    /// <summary>What a server without <c>AgentRegisteredAcked</c> is read as: accepted, though it answered nothing.</summary>
    public static readonly AgentRegisteredResponse Unanswered = new(true) { Answered = false };

    /// <summary>False when no server answered the registration, so nothing it carried is known to have been read.</summary>
    [JsonIgnore] public bool Answered { get; init; } = true;

    /// <summary>A refusal about the reporting connection rather than the agent: the server did not hold it as the
    /// daemon's, so it read nothing the registration carried, and a registration on one it does hold may stand.</summary>
    [JsonIgnore] public bool RefusesConnection =>
        !Registered && RefusedReason is AgentRegistrationRefusal.NotADaemon or AgentRegistrationRefusal.ConnectionVanished;
}
