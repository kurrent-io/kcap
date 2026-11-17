using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// Something the user picked out of the chat so as not to forget to cover it.
public sealed class ChatBookmark(string text, int? message) : ReactiveObject {
    public string Text { get; } = text;

    /// The ordinal of the message it was taken from; null when it came from no message.
    public int? Message { get; } = message;

    ChatBookmarkColor _color;
    public ChatBookmarkColor Color { get => _color; set => this.RaiseAndSetIfChanged(ref _color, value); }
}
