using System.Globalization;
using System.Text.Json;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.GrokBot;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>
/// The Grok Bot desktop app's own copy of each chat, read from disk. The app rewrites a chat's file as
/// the chat grows, so a file's size and write time stand in for the gateway's change markers. The files
/// say nothing about a turn in flight; an entry still streaming says so itself.
/// </summary>
public sealed class GrokBotDesktopChats(string storeDir) : IGrokBotSource {
    public string Description => $"the desktop app's chats in {storeDir}";

    public Task<IReadOnlyList<GrokBotAgent>> ListAgentsAsync(CancellationToken ct) {
        var agents = new List<GrokBotAgent>();

        foreach (var (chatId, file) in Chats()) {
            var info = new FileInfo(file);
            if (!info.Exists) continue;

            agents.Add(new GrokBotAgent(chatId, "", false, string.Create(CultureInfo.InvariantCulture, $"{info.Length}|{info.LastWriteTimeUtc.Ticks}")));
        }

        return Task.FromResult<IReadOnlyList<GrokBotAgent>>(agents);
    }

    public async Task<GrokBotRead> ReadSinceAsync(string agentId, int lastSeq, CancellationToken ct) {
        if (Chats().FirstOrDefault(c => c.ChatId == agentId).File is not { } file) return GrokBotRead.Nothing;

        string text;
        try {
            // The app owns this file and replaces it while it runs.
            text = await File.ReadAllTextSharedAsync(file);
        } catch (IOException) {
            return GrokBotRead.Unreadable;
        }

        JsonDocument doc;
        try {
            doc = JsonDocument.Parse(text);
        } catch (JsonException) {
            // Caught mid-write: the next cycle reads the finished file.
            return GrokBotRead.Unreadable;
        }

        using (doc) {
            if (doc.RootElement.Obj("value")?.Arr("entries") is not { } list) return GrokBotRead.Unreadable;

            var all = new List<GrokBotEntry>();
            foreach (var e in list.EnumerateArray())
                if (GrokBotEntry.TryParse(e, agentId) is { } entry) all.Add(entry);

            // The app keeps a long chat's newest entries only; a copy that opens at seq 1 has the whole chat.
            var fromStart = all.Count == 0 || all.Min(e => e.Seq) <= 1;

            return new GrokBotRead([.. all.Where(e => e.Seq > lastSeq)], fromStart) {
                RolledPast = lastSeq >= 0 && all.Count > 0 && all.Min(e => e.Seq) > lastSeq
            };
        }
    }

    IEnumerable<(string ChatId, string File)> Chats() {
        if (!Directory.Exists(storeDir)) yield break;

        foreach (var file in Directory.EnumerateFiles(storeDir, "*.blob")) {
            if (GrokBotDesktopPaths.ChatIdOf(Path.GetFileName(file)) is { } chatId) yield return (chatId, file);
        }
    }
}
