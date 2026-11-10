namespace Capacitor.Cli.Core.Http;

/// <summary>What starting a pass or message came back as. A refusal is a value: each is something the sender can act
/// on.</summary>
public abstract record PassSendResult {
    public sealed record Sent(StartPassResponseDto Pass) : PassSendResult;

    /// <summary>Held until the sender approves the sharing it names.</summary>
    public sealed record AwaitingApproval(StartPassResponseDto Pass) : PassSendResult;

    public sealed record Refused(PassRefusalDto Refusal) : PassSendResult;

    /// <summary>No such item, or one the sender cannot see; the server does not tell them apart.</summary>
    public sealed record NotFound : PassSendResult;
}
