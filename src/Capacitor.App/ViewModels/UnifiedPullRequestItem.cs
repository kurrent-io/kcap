using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

public sealed class UnifiedPullRequestItem : ReactiveObject {
    UnifiedPullRequestGroup? _replies;
    bool _canToggleReplies;

    internal UnifiedPullRequestItem(UnifiedPullRequestGroup group, PullRequestRow row) {
        Group = group;
        Row = row;
    }

    public UnifiedPullRequestGroup Group { get; }
    public PullRequestRow Row { get; }
    public UnifiedPullRequestGroup? Replies => _replies;
    public bool HasReplies => Replies is not null;
    public bool CanToggleReplies => _canToggleReplies;
    public bool RepliesExpanded {
        get => HasReplies;
        set { if (value != HasReplies && CanToggleReplies) Group.Page.ToggleReplies(Row.Id); }
    }
    public string RepliesLabel => HasReplies ? "Hide replies" : "Show replies";
    public bool HasStatus => Row.Status is not null;
    public bool ShowDetail => Row.Detail.Length > 0 && !Row.IsReviewer;
    public string RepliesAccessibleName => $"{RepliesLabel} for {Row.Title}";

    internal void Update() {
        if (!Row.IsThread) return;
        var expanded = Group.Page.IsExpanded(Row.Id);
        this.RaiseAndSetIfChanged(ref _canToggleReplies, expanded || Group.Page.Context.UnifiedCanRead, nameof(CanToggleReplies));
        if (expanded && _replies is not null || !expanded && _replies is null) {
            _replies?.Update();
            return;
        }
        _replies = expanded ? new(Group.Page, "thread_comments", "Replies", Row.Id, Row.RootCommentId) : null;
        _replies?.Update();
        this.RaisePropertyChanged(nameof(Replies));
        this.RaisePropertyChanged(nameof(HasReplies));
        this.RaisePropertyChanged(nameof(RepliesExpanded));
        this.RaisePropertyChanged(nameof(RepliesLabel));
        this.RaisePropertyChanged(nameof(RepliesAccessibleName));
    }
}
