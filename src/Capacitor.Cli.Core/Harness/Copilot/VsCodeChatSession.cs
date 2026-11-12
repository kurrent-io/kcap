namespace Capacitor.Cli.Core.Harness.Copilot;

/// <summary>What a VS Code chat session log holds once replayed. <paramref name="Skipped"/> names each entry or field
/// left out because its shape was not the one expected; everything else in the log is still read.</summary>
public sealed record VsCodeChatSession(string? CustomTitle, IReadOnlyList<VsCodeChatRequest> Requests, IReadOnlyList<string> Skipped);
