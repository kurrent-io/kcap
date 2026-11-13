namespace Capacitor.App.Views;

public static class ComposerArrowKey {
    /// ↑ on the top line first takes the caret to the start, ↓ on the last line to the end, and
    /// only a press already at that edge steps through history. While an input method is composing,
    /// the arrows belong to its candidate list.
    public static ComposerArrowAction Decide(bool up, int caret, int textLength, int line, int lines, bool composing) {
        if (composing) return ComposerArrowAction.PassThrough;
        if (up) {
            if (line > 0) return ComposerArrowAction.PassThrough;
            return caret > 0 ? ComposerArrowAction.MoveToStart : ComposerArrowAction.RecallOlder;
        }
        if (line < lines - 1) return ComposerArrowAction.PassThrough;
        return caret < textLength ? ComposerArrowAction.MoveToEnd : ComposerArrowAction.RecallNewer;
    }
}
