using System.Text.Json;
using Google.Protobuf.WellKnownTypes;
using Kurrent.Agent.Schema.Events;

namespace Capacitor.Models.Transcripts.Harness.Antigravity;

/// <summary>
/// Antigravity's two interaction tools as canonical interrupts: <c>ask_question</c> asks for input,
/// <c>ask_permission</c> for permission. Their <c>claude_code</c> blocks are the shapes the server writes for every
/// vendor's interrupts, every field present, null when unset. Whatever shape the arguments take, an interrupt comes
/// back: falling back to a tool call would queue a call id no result step answers, and the next real result
/// would pair with it.
/// </summary>
static class AntigravityInterrupts {
    public const string AskQuestion   = "ask_question";
    public const string AskPermission = "ask_permission";

    const string ClaudeCodeSlug = "claude_code";

    /// The tool name every elicitation carries, so readers tell it from a plain wait for input.
    const string ElicitationToolName = "AskUserQuestion";

    /// Only the first question is asked, as with Claude's AskUserQuestion.
    public static InterruptIssued Question(string requestId, JsonElement? args, DateTimeOffset at) {
        var question = "";
        var multi    = false;
        var options  = new List<Value>();

        if (args?.Arr("questions")?.EnumerateArray().FirstOrDefault() is { IsObject: true } first) {
            question = first.Str("question") ?? "";
            multi    = first.Bool("is_multi_select") == true;

            if (first.Arr("options") is { } labels)
                foreach (var label in labels.EnumerateArray())
                    if (label.IsString && label.GetString() is { Length: > 0 } text) options.Add(Value.ForStruct(Option(text)));
        }

        var elicitation = new Struct();
        elicitation.Fields["options"]                = Value.ForList([.. options]);
        elicitation.Fields["is_multi_select"]        = Value.ForBool(multi);
        elicitation.Fields["selected_index"]         = Value.ForNull();
        elicitation.Fields["selected_option_ids"]    = Value.ForNull();
        elicitation.Fields["selected_option_labels"] = Value.ForNull();
        elicitation.Fields["min_selections"]         = Value.ForNull();
        elicitation.Fields["max_selections"]         = Value.ForNull();

        var issued = new InterruptIssued {
            RequestId = requestId,
            Kind      = "input",
            ToolName  = ElicitationToolName,
            Prompt    = question,
            Timestamp = Timestamp.FromDateTimeOffset(at),
        };
        issued.Extensions[ClaudeCodeSlug]            = Block("elicitation", elicitation);
        issued.Extensions[AntigravityExtension.Slug] = AntigravityExtension.Interaction(AskQuestion);
        return issued;
    }

    /// The resource asked for is the interrupt's tool name; the whole arguments ride as its tool input.
    public static InterruptIssued Permission(string requestId, JsonElement? args, DateTimeOffset at) {
        var toolName = args?.Str("Target") ?? args?.Str("Action") ?? AskPermission;

        var permission = new Struct();
        permission.Fields["tool_name"]          = Value.ForString(toolName);
        permission.Fields["tool_input"]         = args is { } input ? Value.Parser.ParseJson(input.GetRawText()) : Value.ForNull();
        permission.Fields["rule_id"]            = Value.ForNull();
        permission.Fields["suggestion_type"]    = Value.ForNull();
        permission.Fields["behavior"]           = Value.ForNull();
        permission.Fields["updated_tool_input"] = Value.ForNull();
        permission.Fields["options"]            = Value.ForNull();

        var issued = new InterruptIssued {
            RequestId = requestId,
            Kind      = "permission",
            ToolName  = toolName,
            Timestamp = Timestamp.FromDateTimeOffset(at),
        };
        issued.Extensions[ClaudeCodeSlug]            = Block("permission", permission);
        issued.Extensions[AntigravityExtension.Slug] = AntigravityExtension.Interaction(AskPermission);
        return issued;
    }

    static Struct Option(string label) {
        var option = new Struct();
        option.Fields["label"]       = Value.ForString(label);
        option.Fields["description"] = Value.ForNull();
        option.Fields["option_id"]   = Value.ForNull();
        option.Fields["kind"]        = Value.ForNull();
        return option;
    }

    static Struct Block(string field, Struct value) {
        var block = new Struct();
        block.Fields[field] = Value.ForStruct(value);
        return block;
    }
}
