using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels;

/// A held message as its owner or its sender sees it. The owner decides a waiting message; its sender may withdraw it.
public sealed class HeldMessageItemViewModel : ReactiveObject, IDisposable {
    readonly ListedHeldMessageDto _message;
    readonly bool _viewerIsOwner;
    readonly BehaviorSubject<bool> _busy = new(false);
    bool _isBusy;
    bool _disposed;

    public Guid DispatchId => _message.DispatchId;
    public string Sender => _viewerIsOwner ? _message.SenderName ?? _message.SenderUserId : "You";
    public string Text => _message.Text;
    public string? AttachmentNames =>
        _message.Attachments.Length == 0 ? null : string.Join(", ", _message.Attachments.Select(a => a.FileName));
    public bool IsWaiting => _message.State == "held";
    public bool CanDecide => _viewerIsOwner && IsWaiting;
    public bool CanWithdraw => !_viewerIsOwner && IsWaiting;

    public string StatusText => (_message.State, _message.Outcome) switch {
        ("held", _) => _viewerIsOwner ? "Waiting for your approval" : "Waiting for the owner's approval",
        ("rejected", _) => "Rejected",
        ("withdrawn", _) => "Withdrawn",
        ("discarded", _) => "Discarded",
        ("dispatched", "sent") => "Sent",
        ("dispatched", "not_delivered") => "Not delivered",
        ("dispatched", "delivery_unknown") => "Delivery unknown",
        _ => "Sending",
    };

    public bool IsBusy {
        get => _isBusy;
        internal set {
            // A refresh can replace the item while its decision is still in flight.
            if (_disposed) return;
            this.RaiseAndSetIfChanged(ref _isBusy, value);
            _busy.OnNext(value);
        }
    }

    public ReactiveCommand<Unit, Unit> ApproveCommand { get; }
    public ReactiveCommand<Unit, Unit> RejectCommand { get; }
    public ReactiveCommand<Unit, Unit> WithdrawCommand { get; }

    internal HeldMessageItemViewModel(ListedHeldMessageDto message, bool viewerIsOwner, Func<HeldMessageItemViewModel, HeldMessageDecision, Task> decide) {
        _message = message;
        _viewerIsOwner = viewerIsOwner;
        var idle = _busy.Select(busy => !busy);
        ApproveCommand = ReactiveCommand.CreateFromTask(() => decide(this, HeldMessageDecision.Approve), idle.Select(i => i && CanDecide));
        RejectCommand = ReactiveCommand.CreateFromTask(() => decide(this, HeldMessageDecision.Reject), idle.Select(i => i && CanDecide));
        WithdrawCommand = ReactiveCommand.CreateFromTask(() => decide(this, HeldMessageDecision.Withdraw), idle.Select(i => i && CanWithdraw));
    }

    public void Dispose() {
        if (_disposed) return;
        _disposed = true;
        ApproveCommand.Dispose();
        RejectCommand.Dispose();
        WithdrawCommand.Dispose();
        _busy.Dispose();
    }
}
