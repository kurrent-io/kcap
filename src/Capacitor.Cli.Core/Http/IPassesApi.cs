namespace Capacitor.Cli.Core.Http;

/// <summary>Our own server's <c>/api/passes</c>, its people picker and its surfaces read — see
/// <see cref="ISessionsApi"/> for the no-URL-composition, no-status-inspection contract every API interface in this
/// namespace follows.</summary>
public interface IPassesApi {
    /// <summary>Whether the server offers passes to this caller. A server without the surfaces route answers no. Writes nothing
    /// to stderr, a lapsed login included: a command hidden while the answer is no prints only its own answer.</summary>
    Task<bool> PassesOfferedAsync(CancellationToken ct = default);

    /// <summary>Colleagues matching <paramref name="query"/>, best first.</summary>
    Task<IReadOnlyList<UserCandidateDto>> SearchColleaguesAsync(string query, CancellationToken ct = default);

    /// <summary>Sent once: a lost answer must not become a second pass.</summary>
    Task<PassSendResult> SendAsync(StartPassBody body, CancellationToken ct = default);

    Task<PassAnswerResult> ApproveAsync(string passId, string via, CancellationToken ct = default);

    Task<PassAnswerResult> DeclineAsync(string passId, CancellationToken ct = default);

    Task<PassReadResult> GetAsync(string passId, CancellationToken ct = default);

    /// <summary>Declares the reminder to follow up a pass as a loose end on the sending session. Throws
    /// <see cref="CapacitorApiException"/> when the server does not record it.</summary>
    Task DeclareFollowUpAsync(DeclareLooseEndBody body, CancellationToken ct = default);
}
