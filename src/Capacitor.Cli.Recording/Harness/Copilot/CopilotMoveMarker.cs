using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Harness.Copilot;

namespace Capacitor.Cli.Recording.Harness.Copilot;

/// <summary>Stages the marker naming the Local chat a moved chat came from: right after the resume that ends its copied
/// history when that resume is in the drain, or first in a later drain when the Local chat is found only then, such as
/// after a watcher restart or while its chat log is unreadable. The server takes a late marker as the end of the history
/// too, so it is never sent before that resume. Without it the history is still left out but the chat stands alone.</summary>
public static class CopilotMoveMarker {
    /// <summary>A Local chat not found is looked for again this often, this many times.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromMinutes(1);

    public const int MaxLookups = 10;

    /// <summary>Never throws: the link is best-effort.</summary>
    public static void Append(
            WatchState state, List<string> newLines, List<int> newLineNumbers, string transcriptPath, string sessionId,
            IReadOnlyList<string> workspaceStorageDirs, DateTimeOffset now, Action<string> log
        ) {
        state.CopilotMoveMarkerStaged = false;

        if (state.CopilotMoveMarkerSent || state.CopilotTranscriptMoved == false || now < state.CopilotMoveNextLookupAt) return;

        try {
            using var lines = File.ReadLinesShared(transcriptPath).GetEnumerator();
            if (!lines.MoveNext()) return;

            state.CopilotTranscriptMoved ??= CopilotMovedChat.IsMoved(lines.Current);
            if (state.CopilotTranscriptMoved == false) return;

            if (CopilotMovedChat.Read(File.ReadLinesShared(transcriptPath)) is not { } history) {
                state.CopilotMoveNextLookupAt = now + RetryEvery;
                return;
            }

            if (CopilotMovedChat.FindLocalChat(workspaceStorageDirs, history).Found is not { } local) {
                if (!history.Matchable || ++state.CopilotMoveLookups >= MaxLookups) {
                    state.CopilotMoveMarkerSent = true;
                    log("Moved VS Code chat: no single Local chat matches its first prompt; storing it unlinked");
                }

                state.CopilotMoveNextLookupAt = now + RetryEvery;
                return;
            }

            var resume = history.ResumeId is { } id ? newLines.FindIndex(l => CopilotMovedChat.IsResume(l, id)) : -1;

            // A resume written after this drain's read is sent by a later drain, which carries the marker after it.
            if (resume < 0 && (state.LineMap?.ToSent(history.ResumeLine) ?? history.ResumeLine) >= state.LinesProcessed) return;

            newLines.Insert(resume + 1, CopilotMovedChat.Marker(sessionId, local, history.ResumedAt));
            newLineNumbers.Insert(resume + 1, CopilotMovedChat.MarkerLineNumber);
            state.CopilotMoveMarkerStaged = true;
        } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) {
            log($"Moved VS Code chat: reading its transcript failed: {ex.Message}");
            state.CopilotMoveNextLookupAt = now + RetryEvery;
        }
    }
}
