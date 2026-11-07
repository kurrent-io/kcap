namespace Capacitor.Cli.Core.Http;

/// <summary>What the sender's approve or decline came back as.</summary>
public abstract record PassAnswerResult {
    public sealed record Answered(PassApprovalResponseDto Answer) : PassAnswerResult;

    public sealed record Refused(PassRefusalDto Refusal) : PassAnswerResult;
}
