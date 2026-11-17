using System.Reactive;
using System.Reactive.Disposables.Fluent;
using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.WorkItems;
using Lucide.Avalonia;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// The header's part chip: which part of its work item the session is in, and the person's moves between them.
public sealed partial class WorkContextViewModel {
    public const string NoPartLabel = "No part";
    public const string OnWorkItemLabel = "On the work item, no part";
    const string UntitledPart = "Untitled session";

    IWorkItemPageApi? _workItems;
    Func<string>? _sessionTitle;
    IAppNotifier? _notifier;
    WorkContextPartsDto? _partsRead;

    bool _showsPartChip;
    public bool ShowsPartChip { get => _showsPartChip; private set => this.RaiseAndSetIfChanged(ref _showsPartChip, value); }
    string _partChipLabel = NoPartLabel;
    public string PartChipLabel { get => _partChipLabel; private set => this.RaiseAndSetIfChanged(ref _partChipLabel, value); }
    WorkStatus? _partChipStatus;
    public WorkStatus? PartChipStatus {
        get => _partChipStatus;
        private set {
            this.RaiseAndSetIfChanged(ref _partChipStatus, value);
            this.RaisePropertyChanged(nameof(HasPartChipStatus));
            this.RaisePropertyChanged(nameof(PartChipIcon));
        }
    }
    public bool HasPartChipStatus => PartChipStatus is not null;
    public LucideIconKind PartChipIcon => (PartChipStatus ?? WorkStatus.Open).Icon();
    IReadOnlyList<SessionPartOption> _partOptions = [];
    public IReadOnlyList<SessionPartOption> PartOptions { get => _partOptions; private set => this.RaiseAndSetIfChanged(ref _partOptions, value); }
    bool _isChangingPart;
    public bool IsChangingPart { get => _isChangingPart; private set => this.RaiseAndSetIfChanged(ref _isChangingPart, value); }

    public ReactiveCommand<SessionPartOption, Unit> MoveToPartCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> NewPartFromSessionCommand { get; private set; } = null!;
    public ReactiveCommand<Unit, Unit> RemoveFromWorkItemCommand { get; private set; } = null!;

    void InitializePartChip(IWorkItemPageApi? workItems, Func<string>? sessionTitle, IAppNotifier? notifier) {
        _workItems = workItems;
        _sessionTitle = sessionTitle;
        _notifier = notifier;
        var idle = this.WhenAnyValue(x => x.IsChangingPart, busy => !busy);
        MoveToPartCommand = ReactiveCommand.CreateFromTask<SessionPartOption>(option => option.IsCurrent ? Task.CompletedTask
            : ChangePartAsync((api, root, session) => api.MoveSessionAsync(option.WorkItemId, session, CancellationToken.None),
                "Couldn't move the session"), idle).DisposeWith(_disposables);
        NewPartFromSessionCommand = ReactiveCommand.CreateFromTask(() => ChangePartAsync(async (api, root, session) => {
            var title = _sessionTitle?.Invoke() is { Length: > 0 } t ? t.Trim() : UntitledPart;
            return await api.AddPartAsync(root, title, CancellationToken.None) is { } part
                && await api.MoveSessionAsync(part, session, CancellationToken.None);
        }, "Couldn't make a part from this session"), idle).DisposeWith(_disposables);
        RemoveFromWorkItemCommand = ReactiveCommand.CreateFromTask(() => ChangePartAsync(
            (api, root, session) => api.RemoveSessionAsync(root, session, CancellationToken.None),
            "Couldn't remove the session from the work item"), idle).DisposeWith(_disposables);
        workItems?.Changed.ObserveOn(RxSchedulers.MainThreadScheduler)
            .Subscribe(_ => { if (!_tornDown && _reader.HasCurrent) _reader.Refresh(); })
            .DisposeWith(_disposables);
    }

    async Task ChangePartAsync(Func<IWorkItemPageApi, string, string, Task<bool>> change, string failure) {
        if (_workItems is not { } api || _partsRead is not { } read || SessionIds.Canonical(_reader.CurrentState) is not { } session) return;
        IsChangingPart = true;
        try {
            if (!await change(api, read.WorkItem.WorkItemId, session)) _notifier?.Notify(failure);
        } finally {
            IsChangingPart = false;
        }
    }

    /// The part whose visible sessions list this session. A session on the work item itself is in none.
    WorkContextPartDto? SessionPart(WorkContextPartsDto read) {
        var session = SessionIds.Canonical(_reader.CurrentState);
        return read.Parts.FirstOrDefault(p => session is not null && p.SessionIds.Any(id => SessionIds.Canonical(id) == session));
    }

    void RebuildPartChip() {
        var read = IsPartsModel && _workItems is not null ? _partsRead : null;
        ShowsPartChip = read is not null;
        if (read is null) {
            PartOptions = [];
            PartChipStatus = null;
            PartChipLabel = NoPartLabel;
            return;
        }
        var mine = SessionPart(read);
        PartChipLabel = mine?.Title ?? NoPartLabel;
        PartChipStatus = mine is null ? null : WorkStatusExtensions.ParseWorkStatus(mine.Status);
        PartOptions = [
            .. read.Parts.Select(p => new SessionPartOption(p.WorkItemId, p.Title, WorkStatusExtensions.ParseWorkStatus(p.Status), ReferenceEquals(p, mine))),
            new SessionPartOption(read.WorkItem.WorkItemId, OnWorkItemLabel, null, mine is null),
        ];
    }
}
