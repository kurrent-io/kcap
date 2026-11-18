using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>Stages, after the resume that ends a moved chat's copied history, the marker naming the Local chat it came
/// from. The server links the two by it; without it, the history is still left out but the chat stands alone.</summary>
public static class CopilotMoveMarker {
    /// <summary>Never throws: the link is best-effort.</summary>
    public static void Append(
            WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, string sessionId,
            IReadOnlyList<string> workspaceStorageDirs, Action<string> log
        ) {
        state.CopilotMoveMarkerStaged = false;

        try {
            if (!newLines.Any(l => l.Contains("\"session.resume\"", StringComparison.Ordinal))) return;

            if (CopilotMovedChat.Read(File.ReadLinesShared(transcriptPath)) is not { } history) return;

            var at = newLineNumbers.IndexOf(history.ResumeLine);
            if (at < 0) return;

            if (CopilotMovedChat.FindLocalChat(workspaceStorageDirs, history) is not { } local) {
                log("Moved VS Code chat: no single Local chat matches its first prompt; storing it unlinked");
                return;
            }

            newLines.Insert(at + 1, CopilotMovedChat.Marker(sessionId, local, history.ResumedAt));
            newLineNumbers.Insert(at + 1, CopilotMovedChat.MarkerLineNumber);
            state.CopilotMoveMarkerStaged = true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            log($"Moved VS Code chat: reading its transcript failed: {ex.Message}");
        }
    }
}
