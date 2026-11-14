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

    public static Guid ClaudeFallback(int lineNumber, string line) => NumberedLine(lineNumber, line);

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

    /// A Gemini message record's own `id` when it parses as a Guid (dashed on an appended record,
    /// 32 hex on a seeded one), else the hash of its line. Each part the record emits is a
    /// <see cref="Sibling"/> of it.
    public static Guid GeminiRecord(string? id, string line) =>
        id is not null && Guid.TryParse(id, out var parsed) ? parsed : Hash(Encoding.UTF8.GetBytes(line));

    /// A legacy <c>messages.jsonl</c> line, or a unified entry without an <c>id</c>: neither carries an id
    /// or a time, so the line number joins the bytes and a repeated message ("yes") is not deduped into
    /// the first.
    public static Guid VibeRecord(int lineNumber, string line) => NumberedLine(lineNumber, line);

    /// One call of a legacy assistant line, keyed by the call's own id under its line's record id.
    public static Guid VibeToolCall(Guid recordId, string callId) => Sibling(recordId, "tool:" + callId);

    /// A unified-store entry carries its own stable <c>id</c> that survives the entry moving from a
    /// journal delta into a published chunk and survives re-import, so its canonical id hashes that id
    /// rather than the surrounding bytes (which <c>updatedAt</c>/<c>generationStatus</c> churn). Scoped
    /// by a prefix so an id can never collide with another derivation's input.
    public static Guid VibeEntry(string entryId) => Hash(Encoding.UTF8.GetBytes("vibe-entry:" + entryId));

    /// A Muse record is identified by its stream and its own record id; the prefix keeps the input from
    /// colliding with another derivation's.
    public static Guid MuseRecord(string streamId, string recordId) =>
        Hash(Encoding.UTF8.GetBytes("muse-record:" + streamId + ":" + recordId));

    /// An OpenCode id hashes a key the record carries, unprefixed: a user message's own id (its text
    /// when it has none), or an assistant part's id.
    public static Guid OpenCode(string key) => Hash(Encoding.UTF8.GetBytes(key));

    public static Guid OpenCodeToolCall(string partId) => OpenCode(partId + ":call");

    public static Guid OpenCodeToolResult(string partId) => OpenCode(partId + ":result");

    /// RFC 4122 name-based (v5) UUID: SHA-1 over the namespace's big-endian bytes, then the name's UTF-8.
    public static Guid Uuid5(Guid ns, string name) {
        var input = new byte[16 + Encoding.UTF8.GetByteCount(name)];
        ns.TryWriteBytes(input, bigEndian: true, out _);
        Encoding.UTF8.GetBytes(name, input.AsSpan(16));

        // The RFC fixes SHA-1 for a v5 UUID.
#pragma warning disable CA5350
        var hash = SHA1.HashData(input);
#pragma warning restore CA5350
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);

        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }

    static readonly Guid PiNamespace = new("7a1c4e92-3b5d-4f86-9c2a-1e8d6b0f5a34");

    /// A Pi event: v5 under Pi's namespace over <c>{sessionId}:{scope}:{kind}</c>. An entry id is unique only
    /// within its session file, so the session id salts it; the scope is the entry id, or <c>session</c> for the
    /// header, and the kind names the event the entry produced.
    public static Guid PiEntry(string sessionId, string scope, string kind) =>
        Uuid5(PiNamespace, string.Concat(sessionId, ":", scope, ":", kind));

    /// A Pi event whose line names no entry id, or whose stream has no session id: the line's UTF-8, then <c>:{kind}</c>.
    public static Guid PiLine(string line, string kind) => Hash(Encoding.UTF8.GetBytes(string.Concat(line, ":", kind)));

    /// A Pi session header that names no session id: the line's UTF-8.
    public static Guid PiHeaderLine(string line) => Hash(Encoding.UTF8.GetBytes(line));

    static Guid NumberedLine(int lineNumber, string line) =>
        Hash(Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{lineNumber} {line}")));
}
