using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Skills;

namespace Capacitor.App.Services;

/// The chat's topics and bookmarks on disk, one file per transcript, named by a hash of its feed key.
/// Reads are small and happen once per transcript; writes run off the UI thread, latest state first.
/// A file that is missing or unreadable reads as no marks.
public sealed partial class ChatMarksFiles(string directory) {
    readonly Lock _gate = new();
    readonly Dictionary<string, Entry?> _pending = new(StringComparer.Ordinal);
    Task _writing = Task.CompletedTask;

    public Entry? Read(string feed) {
        try {
            var path = PathOf(feed);
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize(File.ReadAllText(path), ChatMarksJsonContext.Default.Entry);
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) {
            Console.Error.WriteLine($"kcap: chat marks unreadable for {feed}: {ex.Message}");
            return null;
        }
    }

    /// Queues the feed's marks to be written; null deletes its file.
    public void Write(string feed, Entry? entry) {
        lock (_gate) {
            var idle = _pending.Count == 0;
            _pending[feed] = entry;
            if (idle) _writing = _writing.ContinueWith(_ => Drain(), TaskScheduler.Default);
        }
    }

    /// Completes once every queued write has landed.
    public Task FlushAsync() {
        lock (_gate) return _writing;
    }

    void Drain() {
        while (true) {
            KeyValuePair<string, Entry?> next;
            lock (_gate) {
                if (_pending.Count == 0) return;
                next = _pending.First();
                _pending.Remove(next.Key);
            }
            try {
                var path = PathOf(next.Key);
                if (next.Value is null) File.Delete(path);
                else {
                    Directory.CreateDirectory(directory);
                    AtomicFile.Replace(path, JsonSerializer.Serialize(next.Value, ChatMarksJsonContext.Default.Entry));
                }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
                Console.Error.WriteLine($"kcap: failed to save chat marks for {next.Key}: {ex.Message}");
            }
        }
    }

    string PathOf(string feed) =>
        Path.Join(directory, Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(feed))) + ".json");

    /// A message is named by its line's position in the feed and its index on that line, written
    /// "line.index", with the start of its text as the anchor that confirms it is still the same message.
    public sealed record Entry(int Version, string Feed, DateTimeOffset UpdatedAt, IReadOnlyList<Topic> Topics, IReadOnlyList<Bookmark> Bookmarks);

    /// A topic starting at a message, or with <see cref="End"/>, the end of the topic before it.
    public sealed record Topic(string Message, string? Anchor, string? Name = null, bool? Folded = null, bool? End = null);

    /// Bookmarks keep the order they were taken in; no quote marks the whole message, no message marks text from none.
    /// The colour is its lowercase name, and one this build does not know reads as blue.
    public sealed record Bookmark(string Text, string Color, string? Message = null, string? Anchor = null, string? Quote = null);

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
    [JsonSerializable(typeof(Entry))]
    partial class ChatMarksJsonContext : JsonSerializerContext;
}
