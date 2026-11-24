namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>Where the watcher reads chats from: the cloud computer's gateway, or the desktop app's own
/// copy of each chat. Both hand over the same entries, so a chat read from either lands in the same
/// sessions.</summary>
public interface IGrokBotSource {
    /// <summary>What is being read, for the watcher's first log line.</summary>
    string Description { get; }

    Task<IReadOnlyList<GrokBotAgent>> ListAgentsAsync(CancellationToken ct);

    /// <summary>Every entry the source holds after <paramref name="lastSeq"/>.</summary>
    Task<GrokBotRead> ReadSinceAsync(string agentId, int lastSeq, CancellationToken ct);
}
