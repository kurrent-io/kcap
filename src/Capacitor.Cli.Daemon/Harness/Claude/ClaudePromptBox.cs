namespace Capacitor.Cli.Daemon.Harness.Claude;

/// Reads whether Claude Code's prompt box is on the live screen: a full-width rule above and below a
/// row that starts with the prompt mark. A dialog takes its place, and Claude draws it again only once
/// it is ready to take typed input.
internal static class ClaudePromptBox {
    const char Prompt = '❯';

    // The box grows with the text typed into it; past this it is not the box.
    const int MaxRows = 12;

    internal static bool IsShown(string screen) {
        var lines  = screen.Split('\n');
        var bottom = Array.FindLastIndex(lines, IsEdge);
        if (bottom < 1) return false;

        var top = Array.FindLastIndex(lines, bottom - 1, IsEdge);
        if (top < 0 || bottom - top > MaxRows) return false;

        return lines[top + 1].StartsWith(Prompt);
    }

    static bool IsEdge(string line) {
        var trimmed = line.TrimEnd();
        return trimmed.Length >= 8 && trimmed.All(c => c is '─' or '━');
    }
}
