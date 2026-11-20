namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>The watcher running on this computer, as it describes itself: the server it posts to, the
/// profile whose sign-in it posts with, and what its last cycle met. Server and profile are null when that
/// description cannot be read or was left by a process that is gone: a watcher is running, and where it
/// posts is unknown.</summary>
/// <param name="AcceptedAt">When the server last accepted a call from this run, if it has.</param>
/// <param name="Failure">Why the last cycle stopped short; null when it did not.</param>
/// <param name="Cycled">Whether a cycle has finished. Until one has, no delivery says nothing.</param>
/// <param name="Unread">Chats the last cycle could not read, or could not clear of secrets in time, and so
/// sent nothing for.</param>
public sealed record GrokBotWatchStatus(string? Server, string? Profile, int? Pid, DateTimeOffset? AcceptedAt = null, string? Failure = null, bool Cycled = false, int Unread = 0) {
    /// <summary>A watcher holds its lock while a server refuses everything it sends, so the sentence about
    /// delivery is what tells a running watcher from a recording one.</summary>
    public static string Describe(GrokBotWatchStatus? status) => status switch {
        null             => "No Grok Bot watcher is running on this computer.",
        { Server: null } => "A Grok Bot watcher is running. Where it posts could not be read.",
        _                => $"A Grok Bot watcher is running (pid {status.Pid}), posting to {status.Server} as profile {status.Profile}. {status.Delivery}"
    };

    string Delivery => this switch {
        { Failure: { } failure }  => $"Its last cycle failed: {failure}.",
        { AcceptedAt: { } at }    => $"The server last accepted a delivery at {at.UtcDateTime:yyyy-MM-dd HH:mm:ss}Z.{(Unread > 0 ? $" {Unreadable}" : "")}",
        { Cycled: true, Unread: > 0 } => Unreadable,
        { Cycled: true }          => "Nothing has needed sending since it started.",
        _                         => "Its first cycle has not finished."
    };

    string Unreadable => $"Its last cycle could not read {Unread} chat{(Unread == 1 ? "" : "s")}, so nothing is sent for {(Unread == 1 ? "it" : "them")} yet.";
}
