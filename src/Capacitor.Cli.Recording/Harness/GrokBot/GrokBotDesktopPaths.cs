using System.Text;
using Capacitor.Cli.Core;

namespace Capacitor.Cli.Recording.Harness.GrokBot;

/// <summary>Where the Grok Bot desktop app keeps its own copy of each chat: one file per chat under its
/// Electron data directory, named by the base32 of a dotted key that ends in the chat's id.</summary>
public static class GrokBotDesktopPaths {
    const string ReplicaMarker = ".transcript.replicas.";
    const string Alphabet      = "abcdefghijklmnopqrstuvwxyz234567";

    /// <summary>The app's chat store, or null where the OS names no application-data directory.</summary>
    public static string? ChatStore(UserHome home) {
#pragma warning disable RS0030 // Roaming AppData is not derivable from a UserHome
        var appData = OperatingSystem.IsWindows() ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : "";
#pragma warning restore RS0030

        var userData = OperatingSystem.IsMacOS()   ? Path.Join(home.Path, "Library", "Application Support")
                     : OperatingSystem.IsWindows() ? (appData.Length > 0 ? appData : null)
                     : Path.Join(home.Path, ".config");

        return userData is null ? null : Path.Join(userData, "Grok Bot", "sand-client-persistence");
    }

    /// <summary>The chat a store file holds, or null for a file that is not a chat.</summary>
    public static string? ChatIdOf(string fileName) {
        if (!fileName.EndsWith(".blob", StringComparison.Ordinal) || Decode(fileName.AsSpan(0, fileName.Length - 5)) is not { } key) return null;

        var at = key.LastIndexOf(ReplicaMarker, StringComparison.Ordinal);

        return at >= 0 && key.Length > at + ReplicaMarker.Length ? key[(at + ReplicaMarker.Length)..] : null;
    }

    /// <summary>Unpadded lowercase base32, the form the app writes; null for any other name.</summary>
    static string? Decode(ReadOnlySpan<char> name) {
        var bytes  = new byte[name.Length * 5 / 8];
        var count  = 0;
        var buffer = 0;
        var bits   = 0;

        foreach (var c in name) {
            var value = Alphabet.IndexOf(c);
            if (value < 0) return null;

            buffer = ((buffer << 5) | value) & 0xFFF;
            bits  += 5;

            if (bits >= 8) {
                bits -= 8;
                bytes[count++] = (byte)(buffer >> bits);
            }
        }

        try {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes, 0, count);
        } catch (DecoderFallbackException) {
            return null;
        }
    }
}
