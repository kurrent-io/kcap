using System.Buffers.Binary;
using System.Globalization;
using System.IO.Hashing;
using System.Security.Cryptography;
using System.Text;

namespace Capacitor.Models.Transcripts;

/// Every id a projection derives. The server's dedup set is keyed by these, so the bytes hashed
/// here are a persistence contract: a Guid contributes its own 16-byte layout, a string its UTF-8.
public static class TranscriptIds {
    public static Guid Hash(ReadOnlySpan<byte> bytes) => new(XxHash128.Hash(bytes));

    public static Guid Sibling(Guid primary, string suffix) {
        var suffixBytes = Encoding.UTF8.GetBytes(suffix);
        var input       = new byte[16 + suffixBytes.Length];
        primary.TryWriteBytes(input);
        suffixBytes.CopyTo(input, 16);
        return Hash(input);
    }

    public static Guid ClaudeFallback(int lineNumber, string line) =>
        Hash(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{lineNumber} {line}")));

    public static Guid ClaudeBlock(Guid recordId, int blockIndex) =>
        Sibling(recordId, string.Create(CultureInfo.InvariantCulture, $"block:{blockIndex}"));

    public static Guid ClaudeAttachment(string idScope, Guid recordId, int blockIndex) {
        var scopeBytes = Encoding.UTF8.GetBytes(idScope);
        var input      = new byte[scopeBytes.Length + 20];
        scopeBytes.CopyTo(input, 0);
        recordId.TryWriteBytes(input.AsSpan(scopeBytes.Length));
        BinaryPrimitives.WriteInt32LittleEndian(input.AsSpan(scopeBytes.Length + 16), blockIndex);
        return Hash(input);
    }

    /// One per cancelled prompt, whichever prompt replaced it: an `r` byte, then the prompt's id.
    public static Guid ClaudeRetraction(Guid cancelledPromptId) {
        var input = new byte[17];
        input[0] = (byte)'r';
        cancelledPromptId.TryWriteBytes(input.AsSpan(1));
        return Hash(input);
    }

    public static Guid CodexRecord(string line) => Hash(Encoding.UTF8.GetBytes(line));

    /// A Vibe record's id is the hash of its exact source line — a legacy <c>messages.jsonl</c> line,
    /// or one flattened entry of the unified store. Identical lines dedup to one event, as with Codex;
    /// the unified reader keeps distinct entries distinct by carrying each entry's own bytes
    /// (<c>createdAt</c>, content) into the line it emits. Derived events off one line take a
    /// <see cref="Sibling"/> suffix.
    public static Guid VibeRecord(string line) => Hash(Encoding.UTF8.GetBytes(line));

    /// A unified-store entry carries its own stable <c>id</c> that survives the entry moving from a
    /// journal delta into a published chunk and survives re-import, so its canonical id hashes that id
    /// rather than the surrounding bytes (which <c>updatedAt</c>/<c>generationStatus</c> churn). Scoped
    /// by a prefix so an id can never collide with another derivation's input.
    public static Guid VibeEntry(string entryId) => Hash(Encoding.UTF8.GetBytes("vibe-entry:" + entryId));

    /// A Muse record is identified by its stream and its own record id; the prefix keeps the input from
    /// colliding with another derivation's.
    public static Guid MuseRecord(string streamId, string recordId) =>
        Hash(Encoding.UTF8.GetBytes("muse-record:" + streamId + ":" + recordId));

    /// Kiro's event ids, and its lifecycle hooks' on the server, are name-based (v5) UUIDs in this
    /// namespace. Changing it orphans every stored Kiro event.
    static readonly Guid KiroNamespace = new("a7e1d4c8-3b95-4f2a-9d6e-2c8b1a0f5e34");

    /// The v5 UUID of <c>{sessionId}:{key}:{kind}</c>, where the key is a message or tool-use id.
    public static Guid KiroRecord(string sessionId, string key, string kind) =>
        Uuid5(KiroNamespace, $"{sessionId}:{key}:{kind}");

    /// A Kiro event with no session or key to name it: the line, then <c>:{kind}</c>.
    public static Guid KiroFallback(string line, string kind) => Hash(Encoding.UTF8.GetBytes($"{line}:{kind}"));

    /// One per turn anchor in a stream, whatever line restates it, so a re-sent backfill dedups.
    public static Guid KiroUsage(string idScope, string messageId) => Hash(Encoding.UTF8.GetBytes($"kiro-usage:{idScope}:{messageId}"));

    /// RFC 4122 name-based UUID: SHA-1 over the namespace's big-endian bytes, then the name's UTF-8.
    static Guid Uuid5(Guid ns, string name) {
        var nameBytes = Encoding.UTF8.GetBytes(name);
        var input     = new byte[16 + nameBytes.Length];
        ns.TryWriteBytes(input, bigEndian: true, out _);
        nameBytes.CopyTo(input, 16);

        // The RFC fixes SHA-1 for v5; another digest would change every stored id.
#pragma warning disable CA5350
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350

        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}
