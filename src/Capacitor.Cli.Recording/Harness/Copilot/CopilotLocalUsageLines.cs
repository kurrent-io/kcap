namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>Synthetic transcript lines for a VS Code Local session's usage, with the requests and the cumulative
/// total they report.</summary>
public sealed record CopilotLocalUsageLines(IReadOnlyList<string> Lines, IReadOnlyList<int> LineNumbers, IReadOnlyList<string> RequestIds, long? NanoAiu);
