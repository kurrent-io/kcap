using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Daemon.Services;

/// <summary>Drops a title the moment before it would go out for a session a person disabled. The loop's tick works from
/// one snapshot of its agents, and a title can reach this a generation after that was taken.</summary>
sealed class DisabledSessionTitles(ITitleServerPort server, Func<string, bool> isDisabled) : ITitleServerPort {
    public Task<string?> GetTitleAsync(string sessionId, CancellationToken ct) => server.GetTitleAsync(sessionId, ct);

    public Task<HarnessTitleOutcome> PushTitleAsync(string sessionId, HarnessTitlePost post, CancellationToken ct) =>
        isDisabled(sessionId) ? Task.FromResult(HarnessTitleOutcome.Refused) : server.PushTitleAsync(sessionId, post, ct);

    public Task<TitleRenameOutcome> RenameAsync(string sessionId, string title, CancellationToken ct) => server.RenameAsync(sessionId, title, ct);
}
