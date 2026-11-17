using System.Text.Json.Nodes;

namespace Capacitor.Cli.Commands;

/// <param name="Name">The <c>serverInfo</c> name and the telemetry server label.</param>
/// <param name="LogLabel">The subcommand, as it appears in <c>kcap mcp {LogLabel}</c> diagnostics.</param>
sealed record McpServerDescriptor(string Name, string LogLabel, string? Instructions, McpTool[] Tools) {
    /// <summary>See <see cref="McpStdioLoop.RunAsync"/>'s <c>orderKey</c>.</summary>
    public Func<JsonObject, string?>? OrderKey { get; init; }

    /// <summary>False answers a request whose <c>method</c> is absent or not a string with "Method not found"
    /// rather than "Invalid request".</summary>
    public bool RejectsNonStringMethod { get; init; } = true;

    /// <summary>Applied once to the session client when the first tool call creates it.</summary>
    public Action<HttpClient>? ConfigureClient { get; init; }
}
