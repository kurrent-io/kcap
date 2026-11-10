namespace Capacitor.Cli.Core.Http;

public abstract record PassWhatElseResult {
    /// <param name="RetryAfter">The server's wait, sent only with <c>limited</c>.</param>
    public sealed record Answered(WhatElseDto Answer, TimeSpan? RetryAfter) : PassWhatElseResult;

    /// <summary>No such pass, one the caller did not receive, or one whose item they can no longer see.</summary>
    public sealed record NotFound : PassWhatElseResult;
}
