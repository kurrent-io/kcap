namespace Capacitor.Cli.Ide;

/// <param name="Status"><c>pending</c>, <c>in_progress</c>, <c>completed</c>, <c>blocked</c> or <c>skipped</c>.</param>
internal sealed record PlanTask(string Id, string Title, string? Note, string Status);
