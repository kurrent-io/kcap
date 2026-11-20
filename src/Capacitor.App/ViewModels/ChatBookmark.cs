using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Something the user picked out of the chat so as not to forget to cover it.
public sealed class ChatBookmark(string text, ChatMessageKey? message, string? quote = null, string? anchor = null) : ReactiveObject {
    public string Text { get; } = text;

    /// The message it was taken from; null when it came from no message, or from one no feed placed.
    public ChatMessageKey? Message { get; } = message;

    /// The text as it was picked, to find again in the message; null when the whole message is bookmarked.
    public string? Quote { get; } = quote;

    /// The start of its message's text when it was taken, to tell that message from another now at its key.
    public string? Anchor { get; } = anchor;

    /// Whether <paramref name="row"/>, the message now at this bookmark's key, is the one it was taken from.
    public bool Fits(ChatItemViewModel row) => Anchor is null || ChatTopics.AnchorOf(row) == Anchor;

    public ChatBookmarkColor Color { get; set => this.RaiseAndSetIfChanged(ref field, value); } = ChatBookmarkColor.Blue;
}
