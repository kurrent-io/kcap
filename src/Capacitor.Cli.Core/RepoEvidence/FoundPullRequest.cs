namespace Capacitor.Cli.Core.RepoEvidence;

/// <param name="CallId">The tool call that created it, when the transcript names one.</param>
public sealed record FoundPullRequest(CreatedPullRequest Pr, string? CallId);
