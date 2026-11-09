using System.Text.Json.Serialization;
using Capacitor.Cli.Core.Handoff;

namespace Capacitor.Cli.Core.LocalIpc;

/// JSON payloads for the hand-off probe frames. snake_case on the wire. Branch is already the
/// target branch the app chose; the daemon maps nothing and copies HeadOwner/HeadRepo into the
/// reply's start. LocalOnly decides from the refs the checkout already holds and contacts no
/// remote; only a daemon advertising HandoffWire.LocalProbeCapability honours it, and one that
/// does not ignores the field and fetches.
public sealed record HandoffProbeRequestDto(
    string RepoPath,
    string RepoOwner,
    string RepoName,
    string? Branch,
    int? PullRequestNumber,
    bool HeadIsFork,
    string? HeadOwner,
    string? HeadRepo,
    IReadOnlyList<string>? SessionCommits,
    bool LocalOnly = false);

/// At most one of Refusal, FetchError, Start and NeedsOrigin is set. Start.SourceSessionId is
/// empty; the caller fills it. Completeness is a HandoffCompleteness name. NeedsOrigin answers a
/// LocalOnly probe whose start only origin can name.
public sealed record HandoffProbeReplyDto(
    string? Refusal,
    string? FetchError,
    HandoffLaunchStart? Start,
    string? Completeness,
    IReadOnlyList<string>? MissingCommits,
    bool NeedsOrigin = false);

/// The outcome the daemon keeps for a successor session; null when it keeps none.
public sealed record HandoffOutcomeReplyDto(HandoffOutcomeDto? Outcome);

public static class HandoffRefusals {
    public const string RepoNotAllowed = "repo_not_allowed";
    public const string RepoMismatch   = "repo_mismatch";
    public const string InvalidTarget  = "invalid_target";
}

public static class HandoffWire {
    /// The HelloReply capability that advertises the HandoffProbe and HandoffOutcomeQuery handlers.
    public const string Capability = "handoff/1";

    /// The HelloReply capability saying the HandoffProbe handler honours LocalOnly.
    public const string LocalProbeCapability = "handoff-local/1";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(HandoffProbeRequestDto))]
[JsonSerializable(typeof(HandoffProbeReplyDto))]
[JsonSerializable(typeof(HandoffOutcomeDto))]
[JsonSerializable(typeof(HandoffOutcomeReplyDto))]
public partial class HandoffIpcJsonContext : JsonSerializerContext;
