using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Capacitor.App.Services;

namespace Capacitor.App.ViewModels;

internal static class ChatReadingAnchor {
    internal static string Proof(ChatItemViewModel row) {
        var (kind, text) = row switch {
            AssistantTextItem reply => ("assistant", reply.Text),
            UserTurnItem prompt => ("user", prompt.Text),
            AssistantThinkingItem thinking => ("thinking", thinking.Text),
            SystemNoteItem note => (note.Label, note.Text),
            ShellCommandItem shell => ("shell", shell.HasCommand ? shell.Command : shell.Output ?? ""),
            ToolGroupItem { Calls.Count: > 0 } group => (group.Calls[0].Name, group.Calls[0].InputJson ?? group.Calls[0].Detail),
            _ => ("", ""),
        };
        var content = string.Concat(kind, "\0", text);
        Span<byte> hash = stackalloc byte[32];
        SHA256.HashData(MemoryMarshal.AsBytes(content.AsSpan()), hash);
        return Convert.ToHexString(hash[..16]);
    }

    internal static (ChatItemViewModel? Row, bool Exact) Find(ChatTopics topics, ChatReadingPosition position) {
        if (position.Key is not { } key) return default;
        ChatItemViewModel? nearest = null;
        ulong distance = ulong.MaxValue;
        long ordinal = long.MaxValue;
        foreach (var row in topics.Everything()) {
            if (row.SelectionKey is not { Line: >= 0 } candidate) continue;
            if (candidate == key)
                return Proof(row) == position.Proof ? (row, true) : default;
            var lines = (ulong)Math.Abs(candidate.Line - key.Line);
            var indices = Math.Abs((long)candidate.Index - key.Index);
            if (lines > distance || lines == distance && indices >= ordinal) continue;
            (nearest, distance, ordinal) = (row, lines, indices);
        }
        return (nearest, false);
    }
}
