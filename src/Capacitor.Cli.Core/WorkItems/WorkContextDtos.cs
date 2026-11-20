using System.Text.Json.Serialization;

namespace Capacitor.Cli.Core.WorkItems;

/// One row of <c>GET /api/work-items/session/{id}</c>. <see cref="Label"/> is the server's display
/// label: <c>"KEY — title"</c> for a keyed item, otherwise the title alone, which may itself be a key.
public sealed record SessionWorkItemAssignmentDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("label")]        public required string Label      { get; init; }
    [JsonPropertyName("source")]       public string?         Source     { get; init; }
    [JsonPropertyName("confidence")]   public double          Confidence { get; init; }
    [JsonPropertyName("is_primary")]   public bool            IsPrimary  { get; init; }
}

public sealed record WorkItemRefDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title      { get; init; }
}

public sealed record WorkItemTopologyPartDto {
    [JsonPropertyName("work_item_id")] public required string WorkItemId { get; init; }
    [JsonPropertyName("title")]        public required string Title      { get; init; }
    [JsonPropertyName("ordinal")]      public int             Ordinal    { get; init; }
}

/// The body of <c>GET /api/work-items/{id}/topology</c>. <see cref="Cycle"/> is
/// none|cyclic|indeterminate. <see cref="Item"/> is nullable on the wire. There is no completion
/// figure: the server does not compute one.
public sealed record WorkItemTopologyDto {
    [JsonPropertyName("parts")]      public List<WorkItemTopologyPartDto> Parts     { get; init => field = value ?? []; } = [];
    [JsonPropertyName("part_of")]    public WorkItemRefDto?               PartOf    { get; init; }
    [JsonPropertyName("blocks")]     public List<WorkItemRefDto>          Blocks    { get; init => field = value ?? []; } = [];
    [JsonPropertyName("blocked_by")] public List<WorkItemRefDto>          BlockedBy { get; init => field = value ?? []; } = [];
    [JsonPropertyName("cycle")]      public string                        Cycle     { get; init; } = "none";
    [JsonPropertyName("item")]       public WorkItemRefDto?               Item      { get; init; }
}

public sealed record SessionRepositoryDto {
    [JsonPropertyName("repo_hash")]  public required string RepoHash  { get; init; }
    [JsonPropertyName("owner")]      public required string Owner     { get; init; }
    [JsonPropertyName("repo_name")]  public required string RepoName  { get; init; }
    [JsonPropertyName("branch")]     public string?         Branch    { get; init; }
    [JsonPropertyName("is_primary")] public bool            IsPrimary { get; init; }
}

public sealed record SessionPullRequestDto {
    [JsonPropertyName("repo_hash")] public required string RepoHash { get; init; }
    [JsonPropertyName("owner")]     public required string Owner    { get; init; }
    [JsonPropertyName("repo_name")] public required string RepoName { get; init; }
    [JsonPropertyName("number")]    public int             Number   { get; init; }
    [JsonPropertyName("url")]       public string?         Url      { get; init; }
    [JsonPropertyName("title")]     public string?         Title    { get; init; }
    [JsonPropertyName("head_ref")]  public string?         HeadRef  { get; init; }
}

/// The subset of <c>GET /api/sessions/{id}/summary</c> the sidebar reads; every other member of
/// the server's record is ignored on deserialization.
public sealed record SessionSummaryDto {
    [JsonPropertyName("session_id")]    public required string                 SessionId    { get; init; }
    [JsonPropertyName("title")]         public string?                         Title        { get; init; }
    [JsonPropertyName("vendor")]        public string?                         Vendor       { get; init; }
    [JsonPropertyName("model")]         public string?                         Model        { get; init; }
    [JsonPropertyName("repo_owner")]    public string?                         RepoOwner    { get; init; }
    [JsonPropertyName("repo_name")]     public string?                         RepoName     { get; init; }
    [JsonPropertyName("repo_branch")]   public string?                         RepoBranch   { get; init; }
    [JsonPropertyName("pr_number")]     public int?                            PrNumber     { get; init; }
    [JsonPropertyName("pr_url")]        public string?                         PrUrl        { get; init; }
    [JsonPropertyName("pr_title")]      public string?                         PrTitle      { get; init; }
    [JsonPropertyName("stats")]         public SessionStatsDto?                Stats        { get; init; }
    [JsonPropertyName("started_at")]    public DateTimeOffset?                 StartedAt    { get; init; }
    [JsonPropertyName("last_event_at")] public DateTimeOffset?                 LastEventAt  { get; init; }
    /// The stored audience override; null means the session follows <see cref="DefaultVisibility"/>.
    [JsonPropertyName("visibility")]         public string?                    Visibility        { get; init; }
    [JsonPropertyName("default_visibility")] public string?                    DefaultVisibility { get; init; }
    [JsonPropertyName("detail_level")]       public string?                    DetailLevel       { get; init; }
    /// The live hosted agent's sharing level, sent only to that agent's owner.
    [JsonPropertyName("agent_visibility")]   public string?                    AgentVisibility   { get; init; }
    [JsonPropertyName("repositories")]  public List<SessionRepositoryDto>      Repositories { get; init => field = value ?? []; } = [];
    [JsonPropertyName("pull_requests")] public List<SessionPullRequestDto>     PullRequests { get; init => field = value ?? []; } = [];
}

/// The 4xx body every <c>/api/work-items*</c> route shares; <c>work_items_not_in_plan</c> is the plan gate.
public sealed record WorkItemErrorDto {
    [JsonPropertyName("error")]   public required string Error   { get; init; }
    [JsonPropertyName("message")] public string?         Message { get; init; }
}
