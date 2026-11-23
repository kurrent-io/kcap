using System.Reactive;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The header above a message the user marked as a change of topic. It holds the topic's rows while
/// the topic is folded.
public sealed class ChatTopicItem : ChatItemViewModel {
    List<ChatItemViewModel> _hidden = [];

    readonly Action<ChatTopicItem, string?> _rename;

    public ChatTopicItem(Action<ChatTopicItem> toggle, Action<ChatTopicItem, string?> rename) {
        ToggleCommand = ReactiveCommand.Create(() => toggle(this));
        _rename = rename;
    }

    public ReactiveCommand<Unit, Unit> ToggleCommand { get; }

    /// The name the user gave the topic; null while it is named after its first message.
    public string? CustomName { get; internal set; }

    /// Starting a rename puts the current name in <see cref="Draft"/>.
    public bool IsRenaming {
        get;
        set {
            if (value && !field) Draft = Label;
            this.RaiseAndSetIfChanged(ref field, value);
        }
    }

    public string Draft { get; set => this.RaiseAndSetIfChanged(ref field, value); } = "";

    /// Ends a rename: a blank name goes back to naming the topic after its first message.
    public void Rename(string? name) {
        IsRenaming = false;
        _rename(this, string.IsNullOrWhiteSpace(name) ? null : name.Trim());
    }

    public string Label {
        get;
        private set {
            if (field == value) return;
            field = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ToggleAccessibleName));
        }
    } = "";

    public string Summary { get; private set => this.RaiseAndSetIfChanged(ref field, value); } = "";

    bool _isExpanded = true;
    public bool IsExpanded {
        get => _isExpanded;
        private set {
            if (_isExpanded == value) return;
            _isExpanded = value;
            this.RaisePropertyChanged();
            this.RaisePropertyChanged(nameof(ToggleAccessibleName));
            this.RaisePropertyChanged(nameof(ToggleTip));
        }
    }

    public string ToggleAccessibleName => $"{ToggleTip} {Label}";

    public string ToggleTip => _isExpanded ? "Collapse" : "Expand";

    /// Whether this is the topic the chat is scrolled to.
    public bool IsCurrent { get; internal set => this.RaiseAndSetIfChanged(ref field, value); }

    /// The rows a folded topic holds.
    public IReadOnlyList<ChatItemViewModel> Hidden => _hidden;

    internal void Name(string label) => Label = label;

    internal void Fold(List<ChatItemViewModel> rows, string summary) {
        _hidden = rows;
        Summary = summary;
        IsExpanded = false;
    }

    internal List<ChatItemViewModel> Unfold() {
        var rows = _hidden;
        _hidden = [];
        IsExpanded = true;
        return rows;
    }
}
