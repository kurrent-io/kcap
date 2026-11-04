using System.Text;
using Capacitor.Cli.Core.RepoEvidence;

namespace Capacitor.Cli.Core.Harness.Claude;

/// <summary>
/// Feeds the checkouts a session's subagents mutate into the session watcher's secondary roots, so
/// a PR on a branch a subagent worked on links to the session. The parent probes each checkout once
/// however many subagents worked in it; a subagent watcher probes none. Each transcript is read from
/// where the previous pass stopped, unless its first bytes changed: a replaced file is read again whole,
/// whatever its length.
/// </summary>
public sealed class ClaudeSubagentCheckouts(string parentTranscriptPath) {
    const int HeadBytes = 4096;

    readonly Dictionary<string, (JsonlTail Tail, byte[] Head)> _transcripts = new(StringComparer.Ordinal);

    /// <param name="final">Also take an unterminated last line that parses whole, as the session's own final drain does.</param>
    public void FeedInto(SecondaryRepoRoots roots, bool final = false) {
        foreach (var path in ClaudeSubagentFiles.Transcripts(parentTranscriptPath)) {
            if (roots.IsFull) return;

            try {
                Feed(path, roots, final);
            } catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
                // fail-open: an unreadable transcript is tried again on the next pass
            }
        }
    }

    void Feed(string path, SecondaryRepoRoots roots, bool final) {
        if (!_transcripts.TryGetValue(path, out var known) || !ReadBytes(path, 0, known.Head.Length).AsSpan().SequenceEqual(known.Head)) {
            known = (new JsonlTail(path), []);
        }

        foreach (var line in known.Tail.ReadAppended().Lines) roots.OnLine("claude", line);

        var head = known.Head.Length < HeadBytes ? ReadBytes(path, 0, (int)Math.Min(HeadBytes, known.Tail.Cursor)) : known.Head;
        _transcripts[path] = (known.Tail, head);

        if (final && Encoding.UTF8.GetString(ReadBytes(path, known.Tail.Cursor, int.MaxValue)) is var last && JsonRecord.IsComplete(last)) {
            roots.OnLine("claude", last);
        }
    }

    static byte[] ReadBytes(string path, long offset, int count) {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var length = (int)Math.Max(0, Math.Min(count, stream.Length - offset));
        var buffer = new byte[length];
        stream.Position = offset;
        var read = 0;
        while (read < length) {
            var n = stream.Read(buffer, read, length - read);
            if (n == 0) break;
            read += n;
        }
        return read == length ? buffer : buffer[..read];
    }
}
