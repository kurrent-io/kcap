namespace Capacitor.App.ViewModels;

/// What the session's agent can be asked to do with a bookmark.
public enum ChatBookmarkAsk { StartSession, FileLooseEnd }

public static class ChatBookmarkAskText {
    /// The message that asks the agent. A bookmark is a fragment of the conversation, so the agent is told
    /// to write what it files or launches from the conversation around it, not from the bookmark alone.
    public static string For(this ChatBookmarkAsk ask, ChatBookmark bookmark) => ask switch {
        ChatBookmarkAsk.StartSession =>
            "Start a new hosted agent session to work on the bookmark below, with kcap's start_agent tool. "
            + "The bookmark alone won't make sense to a new agent, so write its prompt to stand on its own: "
            + "what to do and why, the decisions and context from our conversation that bear on it, the files "
            + "involved, and this session's id so it can read more. Use this session's working directory unless "
            + "the work belongs elsewhere, then tell me what you started.\n\n" + Quoted(bookmark),
        _ =>
            "File the bookmark below as a loose end with kcap's declare_loose_end tool. Write it so it makes "
            + "sense to someone who hasn't read this session: what is left to do and why, drawn from our "
            + "conversation around it.\n\n" + Quoted(bookmark),
    };

    static string Quoted(ChatBookmark bookmark) {
        var lead = bookmark.Quote is null ? "The bookmark marks a whole message, which begins:" : "The bookmarked text:";
        var text = (bookmark.Quote ?? bookmark.Text).Replace("\r\n", "\n").Trim();
        return lead + "\n" + string.Join('\n', text.Split('\n').Select(static line => "> " + line));
    }
}
