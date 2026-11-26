using Capacitor.Remote.Models;

namespace Capacitor.App.Services.Inbox;

/// <summary>The signed-in user's notification inbox, its settings and their pass controls, as the web bell offers
/// them. Mutations answer whether the server recorded the change.</summary>
public interface IInboxApi {
    Task<InboxRead<UserSurfacesResponseDto>>       SurfacesAsync(CancellationToken ct);
    Task<InboxRead<NotificationUnreadCountDto>>    UnreadCountAsync(CancellationToken ct);
    Task<InboxRead<NotificationPageDto>>           InboxAsync(int limit, CancellationToken ct);
    Task<bool>                                     MarkReadAsync(string logicalId, long version, CancellationToken ct);
    Task<bool>                                     DismissAsync(string logicalId, long version, CancellationToken ct);
    Task<bool>                                     MarkAllReadAsync(CancellationToken ct);
    Task<bool>                                     DismissAllAsync(CancellationToken ct);
    Task<InboxRead<NotificationAnswerResponseDto>> AnswerAsync(string logicalId, string answer, string? text, CancellationToken ct);
    Task<InboxRead<NotificationPrefsDto>>          PrefsAsync(CancellationToken ct);
    Task<bool>                                     PutPrefAsync(string type, bool enabled, bool slackEnabled, CancellationToken ct);
    Task<InboxRead<PassControlsDto>>               PassControlsAsync(CancellationToken ct);
    Task<InboxRead<PassControlsDto>>               SetAgentOptOutAsync(bool optedOut, CancellationToken ct);
    Task<InboxRead<PassControlsDto>>               UnmuteAsync(string senderId, CancellationToken ct);

    /// <summary>An absolute address for a path the server returned, or null when it is neither http(s) nor a
    /// server-relative path.</summary>
    string? Resolve(string pathOrUrl);
}
