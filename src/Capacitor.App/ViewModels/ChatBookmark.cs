using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Something the user picked out of the chat so as not to forget to cover it.
public sealed class ChatBookmark(string text, int? message, string? quote = null, string? anchor = null) : ReactiveObject {
    public string Text { get; } = text;

    /// The ordinal of the message it was taken from; null when it came from no message.
    public int? Message { get; } = message;

    /// The text as it was picked, to find again in the message; null when the whole message is bookmarked.
    public string? Quote { get; } = quote;

    /// The start of its message's text when it was taken, to tell that message from one now at the same ordinal.
    public string? Anchor { get; } = anchor;

    /// Whether <paramref name="row"/>, the message now at this bookmark's ordinal, is the one it was taken from.
    public bool Fits(ChatItemViewModel row) => Anchor is null || ChatTopics.AnchorOf(row) == Anchor;

    ChatBookmarkColor _color = ChatBookmarkColor.Blue;
    public ChatBookmarkColor Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }
}
