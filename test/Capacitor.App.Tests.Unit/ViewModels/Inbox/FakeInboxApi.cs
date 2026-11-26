using Capacitor.App.Services.Inbox;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels.Inbox;

sealed class FakeInboxApi : IInboxApi {
    public InboxRead<UserSurfacesResponseDto> Surfaces { get; set; } = new(InboxReadKind.Ready, new() { Notifications = true, Passes = true });
    public InboxRead<NotificationUnreadCountDto> Count { get; set; } = new(InboxReadKind.Ready, new() { Count = 0 });
    public InboxRead<NotificationPageDto> Page { get; set; } = new(InboxReadKind.Ready, new());
    public InboxRead<NotificationAnswerResponseDto> Answer { get; set; } = InboxRead<NotificationAnswerResponseDto>.Failed;
    public InboxRead<NotificationPrefsDto> Prefs { get; set; } = new(InboxReadKind.Ready, new());
    public InboxRead<PassControlsDto> Controls { get; set; } = new(InboxReadKind.Ready, new());
    public bool DismissResult { get; set; } = true;
    /// <summary>When set, count reads wait on it, so a test can push while a read runs.</summary>
    public TaskCompletionSource? CountGate { get; set; }

    public int CountReads, PageReads, DismissAllCalls, PrefsReads;
    public readonly List<string> Calls = [];

    public Task<InboxRead<UserSurfacesResponseDto>> SurfacesAsync(CancellationToken ct) => Task.FromResult(Surfaces);

    public async Task<InboxRead<NotificationUnreadCountDto>> UnreadCountAsync(CancellationToken ct) {
        CountReads++;
        if (CountGate is { } gate) await gate.Task;
        return Count;
    }

    public Task<InboxRead<NotificationPageDto>> InboxAsync(int limit, CancellationToken ct) {
        PageReads++;
        return Task.FromResult(Page);
    }

    public Task<bool> MarkReadAsync(string logicalId, long version, CancellationToken ct) => Record($"read {logicalId}@{version}", true);
    public Task<bool> DismissAsync(string logicalId, long version, CancellationToken ct) => Record($"dismiss {logicalId}@{version}", DismissResult);
    public Task<bool> MarkAllReadAsync(CancellationToken ct) => Record("read-all", true);

    public Task<bool> DismissAllAsync(CancellationToken ct) {
        DismissAllCalls++;
        return Record("dismiss-all", true);
    }

    public Task<InboxRead<NotificationAnswerResponseDto>> AnswerAsync(string logicalId, string answer, string? text, CancellationToken ct) {
        Calls.Add($"answer {logicalId} {answer} {text}");
        return Task.FromResult(Answer);
    }

    public Task<InboxRead<NotificationPrefsDto>> PrefsAsync(CancellationToken ct) {
        PrefsReads++;
        return Task.FromResult(Prefs);
    }

    public Task<bool> PutPrefAsync(string type, bool enabled, bool slackEnabled, CancellationToken ct) =>
        Record($"pref {type} {enabled} {slackEnabled}", true);

    public Task<InboxRead<PassControlsDto>> PassControlsAsync(CancellationToken ct) => Task.FromResult(Controls);

    public Task<InboxRead<PassControlsDto>> SetAgentOptOutAsync(bool optedOut, CancellationToken ct) {
        Calls.Add($"opt-out {optedOut}");
        Controls = new(InboxReadKind.Ready, Controls.Value! with { AgentOptedOut = optedOut });
        return Task.FromResult(Controls);
    }

    public Task<InboxRead<PassControlsDto>> UnmuteAsync(string senderId, CancellationToken ct) {
        Calls.Add($"unmute {senderId}");
        Controls = new(InboxReadKind.Ready, Controls.Value! with { MutedSenders = Controls.Value.MutedSenders.Where(s => s.UserId != senderId).ToList() });
        return Task.FromResult(Controls);
    }

    public string? Resolve(string pathOrUrl) => pathOrUrl.StartsWith('/') ? "https://kcap.example" + pathOrUrl : pathOrUrl;

    Task<bool> Record(string call, bool result) {
        Calls.Add(call);
        return Task.FromResult(result);
    }
}
