namespace Capacitor.Cli.Daemon.Services;

/// <summary>How long a caller waits on each catalog probe. A probe still running at <c>Limit</c> answers
/// null and its key goes to <c>Overran</c>; it is left to finish on its own, and if it then answers with a
/// catalog its key goes to <c>AnsweredLate</c>, since the catalog it would have published is still missing.</summary>
internal sealed record ProbeCeiling(TimeSpan Limit, TimeProvider Time, Action<string> Overran, Action<string>? AnsweredLate = null);
