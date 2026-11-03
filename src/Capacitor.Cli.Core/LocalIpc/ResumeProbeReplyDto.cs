namespace Capacitor.Cli.Core.LocalIpc;

/// Null Refusal: the daemon can resume the session here.
public sealed record ResumeProbeReplyDto(string? Refusal);
