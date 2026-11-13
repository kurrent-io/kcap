namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>Synthetic transcript lines for a VS Code Local session's usage, with the requests whose output they record
/// and the credits they report per request.</summary>
public sealed record CopilotLocalUsageLines(
    IReadOnlyList<string> Lines, IReadOnlyList<int> LineNumbers, IReadOnlyList<string> RequestIds, IReadOnlyList<(string RequestId, long NanoAiu)> Credits);
