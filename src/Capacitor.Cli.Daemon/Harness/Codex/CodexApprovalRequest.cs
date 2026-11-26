using System.Text.Json;

namespace Capacitor.Cli.Daemon.Harness.Codex;

/// <summary>What an app-server approval request asks for, read the same way by the policy and by the card.</summary>
internal static class CodexApprovalRequest {
    /// <summary>Codex's reserved id for the environment the app-server itself runs commands in, which it names on every
    /// command approval there. Any other id is a remote or configured environment.</summary>
    const string LocalEnvironment = "local";

    /// <summary>The command of a command approval that asks for no more than its text says; null for input to a running
    /// command, a managed-network grant, or a command bound for another execution environment.</summary>
    public static string? PlainCommand(JsonElement request) =>
        request.Str("kind") is null or "command" && !Names(request, "networkApprovalContext") && RunsLocally(request)
            ? request.Str("command") is { Length: > 0 } command ? command : null
            : null;

    /// <summary>The request carries the property with a value other than JSON null.</summary>
    public static bool Names(JsonElement request, string property) => request.Prop(property) is { } value && !value.IsNull;

    static bool RunsLocally(JsonElement request) =>
        !Names(request, "environmentId") || request.Str("environmentId") == LocalEnvironment;
}
