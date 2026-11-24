using Capacitor.Models.Transcripts.Harness.Antigravity;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Antigravity;

/// <summary>Each Antigravity tool name maps to the Claude name and input keys the rest of kcap reads, carrying only
/// the keys that map and the ones present. The parameters are the shapes agy and the desktop app write.</summary>
public class AntigravityToolNamesTests {
    [Test]
    [Arguments("run_command", """{"CommandLine":"false","WaitMsBeforeAsync":3000,"toolSummary":"Run false"}""", "Bash", """{"command":"false","description":"Run false"}""")]
    [Arguments("read_file", """{"Path":"/w/notes.txt"}""", "Read", """{"file_path":"/w/notes.txt"}""")]
    [Arguments("read_file", """{"Path":"/w/notes.txt","StartLine":10,"EndLine":19}""", "Read", """{"file_path":"/w/notes.txt","offset":10,"limit":10}""")]
    [Arguments("view_file", """{"AbsolutePath":"/w/a.py","StartLine":3,"toolSummary":"View a.py"}""", "Read", """{"file_path":"/w/a.py","offset":3}""")]
    [Arguments("edit_file", """{"Path":"/w/notes.txt"}""", "Edit", """{"file_path":"/w/notes.txt"}""")]
    [Arguments("edit_file", """{"Path":"/w/notes.txt","Target":"old","Replacement":"new"}""", "Edit", """{"file_path":"/w/notes.txt","old_string":"old","new_string":"new"}""")]
    [Arguments("write_file", """{"Path":"/w/b.txt","Content":"hi","Overwrite":true}""", "Write", """{"file_path":"/w/b.txt","content":"hi"}""")]
    [Arguments("write_to_file", """{"TargetFile":"/w/b.txt","CodeContent":"hi","Overwrite":true}""", "Write", """{"file_path":"/w/b.txt","content":"hi"}""")]
    [Arguments("list_dir", """{"DirectoryPath":"/w","toolAction":"Listing"}""", "LS", """{"path":"/w"}""")]
    [Arguments("grep_search", """{"Query":"alpha","SearchPath":"/w","IsRegex":false}""", "Grep", """{"pattern":"alpha","path":"/w"}""")]
    [Arguments("find_by_name", """{"Pattern":"*.md","SearchDirectory":"/w"}""", "Glob", """{"pattern":"*.md","path":"/w"}""")]
    [Arguments("search_web", """{"query":"agy docs","domain":"example.com"}""", "WebSearch", """{"query":"agy docs"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":{"query":"order history","repo":"all"},"ServerName":"kcap-sessions","ToolName":"search_sessions","toolSummary":"Search"}""", "mcp__kcap-sessions__search_sessions", """{"query":"order history","repo":"all"}""")]
    [Arguments("call_mcp_tool", """{"Arguments":"{\"q\":1}","ServerName":"s","ToolName":"t"}""", "mcp__s__t", """{"q":1}""")]
    [Arguments("call_mcp_tool", """{"ServerName":"s","ToolName":"t"}""", "mcp__s__t", "{}")]
    public async Task A_known_tool_translates_to_its_claude_name_and_input(string native, string parameters, string name, string input) {
        var translated = AntigravityToolNames.Translate(native, parameters);

        await Assert.That(translated.Name).IsEqualTo(name);
        await Assert.That(translated.InputJson).IsEqualTo(input);
    }

    [Test]
    [Arguments("browser_click_element", """{"Index":3}""")]
    [Arguments("call_mcp_tool", """{"ServerName":"s","Arguments":{}}""")]
    public async Task A_tool_it_cannot_translate_keeps_its_name_and_parameters(string native, string parameters) {
        var translated = AntigravityToolNames.Translate(native, parameters);

        await Assert.That(translated.Name).IsEqualTo(native);
        await Assert.That(translated.InputJson).IsEqualTo(parameters);
    }
}
