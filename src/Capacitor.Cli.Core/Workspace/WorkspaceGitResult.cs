namespace Capacitor.Cli.Core.Workspace;

/// <summary>One git run. <see cref="Cut"/> means stdout passed its limit and only its head was kept;
/// <see cref="TimedOut"/> means the deadline killed the run.</summary>
public sealed record WorkspaceGitResult(int ExitCode, string Stdout, string Stderr, bool Cut, bool TimedOut) {
    public bool Ok => ExitCode == 0 && !TimedOut;
}
