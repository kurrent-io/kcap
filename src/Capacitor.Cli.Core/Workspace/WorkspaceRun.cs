namespace Capacitor.Cli.Core.Workspace;

/// <summary>The run of a session a span was opened in, named by the vendor transcript's id for it; a null
/// <paramref name="Id"/> is a run whose transcript could not be read when the span opened.</summary>
public sealed record WorkspaceRun(string? Id);
