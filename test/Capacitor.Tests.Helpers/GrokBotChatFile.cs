using System.Text;

namespace Capacitor.Tests.Helpers;

/// <summary>File names as the Grok Bot desktop app writes them in its chat store: a dotted key in unpadded
/// lowercase base32, with a <c>.blob</c> extension.</summary>
public static class GrokBotChatFile {
    const string Alphabet = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>The file holding the chat <paramref name="chatId"/>.</summary>
    public static string NameOfChat(string chatId) => NameFor($"sand.client.slice.account.github%7Cuser_01TEST.transcript.replicas.{chatId}");

    public static string NameFor(string key) {
        var name   = new StringBuilder();
        var buffer = 0;
        var bits   = 0;

        foreach (var b in Encoding.UTF8.GetBytes(key)) {
            buffer = ((buffer << 8) | b) & 0xFFFF;
            bits  += 8;

            while (bits >= 5) {
                bits -= 5;
                name.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0) name.Append(Alphabet[(buffer << (5 - bits)) & 31]);

        return name.Append(".blob").ToString();
    }
}
