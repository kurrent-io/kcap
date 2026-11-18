using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Something the user picked out of the chat so as not to forget to cover it.
public sealed class ChatBookmark(string text, int? message, string? quote = null) : ReactiveObject {
    public string Text { get; } = text;

    /// The ordinal of the message it was taken from; null when it came from no message.
    public int? Message { get; } = message;

    /// The text as it was picked, to find again in the message; null when the whole message is bookmarked.
    public string? Quote { get; } = quote;

    ChatBookmarkColor _color = ChatBookmarkColor.Blue;
    public ChatBookmarkColor Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }
}
