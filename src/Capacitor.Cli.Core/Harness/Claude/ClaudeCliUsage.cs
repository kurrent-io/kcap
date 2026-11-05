namespace Capacitor.Cli.Core.Harness.Claude;

public record ClaudeCliUsage(long InputTokens, long OutputTokens, long CacheReadTokens, long CacheWriteTokens, double? CostUsd);
