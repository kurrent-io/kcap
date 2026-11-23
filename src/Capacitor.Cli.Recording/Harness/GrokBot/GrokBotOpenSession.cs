namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>The session currently open on one Bot's thread. <see cref="FirstSeq"/> is where it opened:
/// whatever precedes it in the chat was passed over when that entry was chosen.</summary>
public sealed record GrokBotOpenSession(
        string SessionId,
        string FirstEntryId,
        long   StartedAtMs,
        int    FirstSeq,
        long   LastEntryMs
    );
