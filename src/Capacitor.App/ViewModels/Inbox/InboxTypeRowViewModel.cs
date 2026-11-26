using Capacitor.Remote.Models;
using ReactiveUI.Reactive;

namespace Capacitor.App.ViewModels.Inbox;

/// <summary>One notification type's In-app and Slack switches. Flipping either writes both, as the server replaces the
/// pair.</summary>
public sealed class InboxTypeRowViewModel : ReactiveObject {
    readonly Func<InboxTypeRowViewModel, bool, bool, Task> _write;
    bool _applying;

    public string Type         { get; }
    public string DisplayName  { get; }
    public string Description  { get; }
    public bool   SlackCapable { get; }
    public bool   Retired      { get; }
    public bool   SlackUsable  { get; }
    public string InAppName    => $"In-app: {DisplayName}";
    public string SlackName    => SlackCapable ? $"Slack: {DisplayName}" : $"Slack: not available for {DisplayName} (in-app only)";

    public bool Enabled {
        get;
        set {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            if (!_applying) _ = _write(this, value, SlackEnabled);
        }
    }

    public bool SlackEnabled {
        get;
        set {
            if (field == value) return;
            this.RaiseAndSetIfChanged(ref field, value);
            if (!_applying) _ = _write(this, Enabled, value);
        }
    }

    public InboxTypeRowViewModel(NotificationTypePrefDto dto, bool slackConnected, Func<InboxTypeRowViewModel, bool, bool, Task> write) {
        _write       = write;
        Type         = dto.Type;
        DisplayName  = dto.DisplayName;
        Description  = dto.Description;
        SlackCapable = dto.SlackCapable;
        Retired      = dto.Retired;
        SlackUsable  = dto.SlackCapable && slackConnected;
        Apply(dto);
    }

    /// <summary>Takes the server's values without writing them back.</summary>
    public void Apply(NotificationTypePrefDto dto) {
        _applying = true;
        try {
            Enabled      = dto.Enabled;
            SlackEnabled = dto.SlackEnabled;
        } finally {
            _applying = false;
        }
    }
}
