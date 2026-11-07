using System.Collections.Concurrent;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// Answers as configured and records every write it was asked for.
sealed class FakePassesApi : IPassesApi {
    public bool Offered { get; init; } = true;

    public List<UserCandidateDto> Colleagues { get; init; } = [new("github:2", "Sam Jones", null)];

    public PassSendResult SendResult { get; init; } = new PassSendResult.Sent(new("p1", "sent", [], null, null, null));

    public PassAnswerResult AnswerResult { get; init; } =
        new PassAnswerResult.Answered(new("sent", "sent", [], null, null));

    public ConcurrentQueue<StartPassBody> Sent { get; } = new();

    public ConcurrentQueue<(string PassId, string Via)> Approved { get; } = new();

    public ConcurrentQueue<string> Declined { get; } = new();

    public Exception? OfferedFails { get; init; }

    /// Never answers the offer check; only the caller's token ends the wait.
    public bool OfferedHangs { get; init; }

    public Exception? SendFails { get; init; }

    /// Thrown by approve and decline once the call is recorded.
    public Exception? AnswerFails { get; init; }

    /// Approve and decline never answer; only the caller's token ends the wait.
    public bool AnswerHangs { get; init; }

    public Task<bool> PassesOfferedAsync(CancellationToken ct = default) =>
        OfferedHangs ? HangAsync<bool>(ct) : OfferedFails is { } fails ? Task.FromException<bool>(fails) : Task.FromResult(Offered);

    public Task<IReadOnlyList<UserCandidateDto>> SearchColleaguesAsync(string query, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<UserCandidateDto>>(Colleagues);

    public Task<PassSendResult> SendAsync(StartPassBody body, CancellationToken ct = default) {
        Sent.Enqueue(body);

        return SendFails is { } fails ? Task.FromException<PassSendResult>(fails) : Task.FromResult(SendResult);
    }

    public Task<PassAnswerResult> ApproveAsync(string passId, string via, CancellationToken ct = default) {
        Approved.Enqueue((passId, via));

        return Answer(AnswerResult, ct);
    }

    public Task<PassAnswerResult> DeclineAsync(string passId, CancellationToken ct = default) {
        Declined.Enqueue(passId);

        return Answer(new PassAnswerResult.Answered(new("declined", "declined", [], null, null)), ct);
    }

    Task<PassAnswerResult> Answer(PassAnswerResult result, CancellationToken ct) =>
        AnswerHangs ? HangAsync<PassAnswerResult>(ct) : AnswerFails is { } fails ? Task.FromException<PassAnswerResult>(fails) : Task.FromResult(result);

    static async Task<T> HangAsync<T>(CancellationToken ct) {
        await Task.Delay(Timeout.Infinite, ct);

        throw new InvalidOperationException("An infinite delay ended without cancellation");
    }

    public Task<PassReadResult> GetAsync(string passId, CancellationToken ct = default) =>
        Task.FromResult<PassReadResult>(new PassReadResult.NotFound());

    public static PassSendResult AwaitingApproval(string approvalText, params PassSharingGrantDto[] grants) =>
        new PassSendResult.AwaitingApproval(new("p1", "awaiting_approval", [.. grants], approvalText, DateTimeOffset.UnixEpoch, null));
}
