using Capacitor.Models.Transcripts.Harness.Antigravity;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Antigravity;

/// <summary>Each Antigravity tool maps to the Claude name and argument keys the rest of kcap reads, native keys with
/// no Claude counterpart kept beside them, exactly as kcap-server's normalizer maps it. The parameters are the shapes
/// agy and the desktop app write, current and legacy.</summary>
public class AntigravityToolNamesTests {
    [Test]
    [Arguments("run_command", """{"CommandLine":"ls","Cwd":"/w","toolSummary":"List"}""", "Bash", """{"command":"ls","Cwd":"/w","description":"List"}""")]
    [Arguments("read_file", """{"Path":"/w/a.txt","StartLine":3,"EndLine":7}""", "Read", """{"file_path":"/w/a.txt","offset":3,"limit":5}""")]
    [Arguments("read_file", """{"Path":"/w/a.txt"}""", "Read", """{"file_path":"/w/a.txt"}""")]
    [Arguments("view_file", """{"AbsolutePath":"file:///w/a.txt","EndLine":4,"toolSummary":"View"}""", "Read", """{"file_path":"/w/a.txt","toolSummary":"View","limit":4}""")]
    [Arguments("edit_file", """{"Path":"/w/a"}""", "Edit", """{"file_path":"/w/a"}""")]
    [Arguments("edit_file", """{"Path":"/w/a","Target":"x","Replacement":"y"}""", "Edit", """{"file_path":"/w/a","old_string":"x","new_string":"y"}""")]
    [Arguments("write_file", """{"Content":"abc","Overwrite":true,"Path":"/w/n"}""", "Write", """{"content":"abc","Overwrite":true,"file_path":"/w/n"}""")]
    [Arguments("write_to_file", """{"CodeContent":"x","TargetFile":"file:///w/t.py"}""", "Write", """{"content":"x","file_path":"/w/t.py"}""")]
    [Arguments("replace_file_content", """{"TargetFile":"/w/h.py","TargetContent":"a","ReplacementContent":"b","Instruction":"i"}""", "Edit", """{"file_path":"/w/h.py","old_string":"a","new_string":"b","Instruction":"i"}""")]
    [Arguments("replace_file_content", """{"TargetFile":"/w/h.py","ReplacementChunks":[{"TargetContent":"a","ReplacementContent":"b"},{"TargetContent":"c","ReplacementContent":"d"}]}""", "MultiEdit", """{"file_path":"/w/h.py","edits":[{"old_string":"a","new_string":"b"},{"old_string":"c","new_string":"d"}]}""")]
    [Arguments("list_dir", """{"DirectoryPath":"/w"}""", "LS", """{"path":"/w"}""")]
    [Arguments("grep_search", """{"Query":"abc","SearchPath":"/w","IsRegex":false}""", "Grep", """{"pattern":"abc","path":"/w","IsRegex":false}""")]
    [Arguments("find_by_name", """{"Pattern":"*.py","SearchDirectory":"/w"}""", "Glob", """{"pattern":"*.py","path":"/w"}""")]
    [Arguments("search_web", """{"query":"q","domain":"d"}""", "WebSearch", """{"query":"q","domain":"d"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":{"a":1},"ServerName":"kcap-workitems","ToolName":"get_next_work","toolSummary":"s"}""", "mcp__kcap-workitems__get_next_work", """{"a":1}""")]
    [Arguments("call_mcp_tool", """{"Arguments":"{\"a\":1}","ServerName":"linear","ToolName":"get_issue"}""", "mcp__linear__get_issue", """{"a":1}""")]
    [Arguments("call_mcp_tool", """{"ServerName":"s","ToolName":"t"}""", "mcp__s__t", "{}")]
    public async Task A_known_tool_translates_to_its_claude_name_and_input(string native, string parameters, string name, string input) {
        var translated = AntigravityToolNames.Translate(native, parameters);

        await Assert.That(translated.Name).IsEqualTo(name);
        await Assert.That(translated.InputJson).IsEqualTo(input);
    }

    [Test]
    [Arguments("browser_click_element", """{"Index":3}""")]
    [Arguments("send_message", """{"Message":"hi"}""")]
    [Arguments("run_command", """{"cmd":"x"}""")]
    [Arguments("call_mcp_tool", """{"ToolName":"t"}""")]
    [Arguments("replace_file_content", """{"TargetFile":"/w/h.py"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":"not-json","ServerName":"s","ToolName":"t"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":"[1]","ServerName":"s","ToolName":"t"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":7,"ServerName":"s","ToolName":"t"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":[1],"ServerName":"s","ToolName":"t"}""")]
    [Arguments("read_file", "not json")]
    [Arguments("read_file", null)]
    public async Task A_tool_it_cannot_translate_keeps_its_name_and_parameters(string native, string? parameters) {
        var translated = AntigravityToolNames.Translate(native, parameters);

        await Assert.That(translated.Name).IsEqualTo(native);
        await Assert.That(translated.InputJson).IsEqualTo(parameters);
    }
}
