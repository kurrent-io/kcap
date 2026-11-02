using System.Text.Json.Serialization;

namespace Capacitor.Remote.Models;

/// What the caller may do to one hosted agent. Reaches is whether they hold Full on its session; without it nothing
/// else applies. While HoldMessages is on, a caller without SendWithoutApproval has each message held for the owner.
public sealed record AgentViewerRightsDto {
    [JsonPropertyName("is_owner")]              public bool IsOwner { get; init; }
    [JsonPropertyName("reaches")]               public bool Reaches { get; init; }
    [JsonPropertyName("answer_questions")]      public bool AnswerQuestions { get; init; }
    [JsonPropertyName("approve_tool_use")]      public bool ApproveToolUse { get; init; }
    [JsonPropertyName("send_without_approval")] public bool SendWithoutApproval { get; init; }
    [JsonPropertyName("hold_messages")]         public bool HoldMessages { get; init; }
}
