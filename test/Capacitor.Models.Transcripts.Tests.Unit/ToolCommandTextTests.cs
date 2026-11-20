namespace Capacitor.Models.Transcripts.Tests.Unit;

public class ToolCommandTextTests {
    /// <summary>The server reads Antigravity's <c>run_command</c> input from <c>CommandLine</c>, and reads
    /// <c>cmd</c>, then <c>CommandLine</c>, then <c>command</c> when an input carries several.</summary>
    [Test]
    [Arguments("""{"CommandLine":"rg needle src","Cwd":"/w"}""", "rg needle src")]
    [Arguments("""{"cmd":"ls","CommandLine":"pwd","command":"date"}""", "ls")]
    [Arguments("""{"CommandLine":"pwd","command":"date"}""", "pwd")]
    [Arguments("""{"command":"date"}""", "date")]
    [Arguments("""{"command":["bash","-lc","cat a.rs"]}""", "cat a.rs")]
    [Arguments("""{"CommandLine":42}""", null)]
    public async Task Command_text_comes_from_cmd_CommandLine_or_command(string input, string? command) =>
        await Assert.That(ToolCommandText.From(input)).IsEqualTo(command);
}
