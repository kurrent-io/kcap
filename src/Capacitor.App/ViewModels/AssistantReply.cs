using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// One turn of the agent's answer, shared by every row in it. Its time and Copy sit after its last row.
public sealed class AssistantReply : ReactiveObject {
    int _hovers;

    /// The reply's messages in order, without the tool calls, thinking and notes between them.
    public string Text { get; internal set => this.RaiseAndSetIfChanged(ref field, value); } = "";

    public ChatTimestamp? Time { get; internal set => this.RaiseAndSetIfChanged(ref field, value); }

    /// Whether the pointer, or keyboard focus, is on any row of the reply.
    public bool IsActive => _hovers > 0;

    internal void Enter() {
        if (_hovers++ == 0) this.RaisePropertyChanged(nameof(IsActive));
    }

    internal void Leave() {
        if (_hovers == 0) return;
        if (--_hovers == 0) this.RaisePropertyChanged(nameof(IsActive));
    }
}
