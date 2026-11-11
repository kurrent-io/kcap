namespace Capacitor.Cli.Daemon.Harness.Claude;

/// <summary>A session and Claude process someone stopped steering; <paramref name="SessionId"/> is canonical.</summary>
internal sealed record ModStoppedPair(string SessionId, string ProcessKey);
