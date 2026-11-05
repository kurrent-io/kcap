using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

/// Records what voice control asked the app to do.
sealed class FakeVoiceHost : IVoiceHost {
    public List<string> Calls { get; } = [];
    public List<VoiceSession> SessionList { get; set; } = [];
    public string? OpenId { get; set; }
    public string? SendRefusal { get; set; }
    public string? Permission { get; set; }
    public Subject<VoiceNarration> Turn { get; } = new();

    public void ShowWindow() => Calls.Add("show");
    public IReadOnlyList<VoiceSession> Sessions => SessionList.Select(s => s with { IsOpen = s.AgentId == OpenId }).ToList();
    public VoiceSession? OpenSession => Sessions.FirstOrDefault(s => s.IsOpen);

    public bool Open(string agentId) {
        Calls.Add($"open {agentId}");
        OpenId = agentId;
        return true;
    }

    public void GoHome() {
        Calls.Add("home");
        OpenId = null;
    }

    public bool ShowTab(VoiceTab tab) {
        Calls.Add($"tab {tab}");
        return true;
    }

    public Task<string?> SendPromptAsync(string text) {
        Calls.Add($"send {text}");
        return Task.FromResult(SendRefusal);
    }

    public bool Dictate(string text) {
        Calls.Add($"dictate {text}");
        return true;
    }

    public Task<string?> SendDraftAsync() {
        Calls.Add("send draft");
        return Task.FromResult<string?>(null);
    }

    public Task<string?> StartSessionAsync(string prompt) {
        Calls.Add($"start {prompt}");
        return Task.FromResult<string?>(null);
    }

    public Task<string?> SelectRepositoryAsync(string query) => Task.FromResult<string?>(null);
    public Task<string?> SelectHarnessAsync(string query) => Task.FromResult<string?>(null);
    public string LauncherSummary => "In kcap-cli, with Claude.";

    public bool StopAgent() {
        Calls.Add("stop");
        return true;
    }

    public bool Interrupt() {
        Calls.Add("interrupt");
        return true;
    }

    public string? PendingPermission => Permission;

    public Task<bool> AnswerPermissionAsync(PermissionAnswer answer) {
        Calls.Add($"permission {answer}");
        Permission = null;
        return Task.FromResult(true);
    }

    public string? LastReply => "Done. **All tests pass.**";
    public string StatusSummary => "Daemon connected.";
    public void OpenSettings() => Calls.Add("settings");
    public bool Refresh() => true;
    public void Zoom(int direction) => Calls.Add($"zoom {direction}");
    public IObservable<VoiceNarration> WatchNextTurn() => Turn;
}
