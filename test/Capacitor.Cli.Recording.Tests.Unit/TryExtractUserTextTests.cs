namespace Capacitor.Cli.Recording.Tests.Unit;

public class TryExtractUserTextTests {
    [Test]
    [Arguments("""{"type":"user","message":{"content":"hello world"}}""", "hello world")]
    [Arguments("""{"type":"user","message":{"content":"fix the bug"}}""", "fix the bug")]
    public async Task StringContent_ReturnsText(string line, string expected) {
        var result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo(expected);
    }

    [Test]
    public async Task ArrayContent_ReturnsFirstTextElement() {
        const string line   = """{"type":"user","message":{"content":[{"type":"text","text":"from array"}]}}""";
        var          result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("from array");
    }

    [Test]
    public async Task ArrayContent_SkipsNonTextElements() {
        const string line   = """{"type":"user","message":{"content":[{"type":"image","url":"x"},{"type":"text","text":"second"}]}}""";
        var          result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsEqualTo("second");
    }

    [Test]
    [Arguments("""{"type":"assistant","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"system","message":{"content":"hi"}}""")]
    [Arguments("""{"type":"user","isMeta":true,"message":{"content":"meta stuff"}}""")]
    [Arguments("""{"type":"user","message":{"content":"<local-command-stdout>some output"}}""")]
    [Arguments("""{"type":"user","message":{"content":[{"type":"text","text":"<local-command-stdout>output"}]}}""")]
    [Arguments("not json at all")]
    [Arguments("")]
    [Arguments("{}")]
    [Arguments("""{"type":"user"}""")]
    [Arguments("""{"type":"user","message":{}}""")]
    [Arguments("""{"type":"user","message":{"content":[]}}""")]
    public async Task ReturnsNull_ForInvalidOrFilteredInput(string line) {
        var result = TranscriptLineText.TryExtractUserText(line);
        await Assert.That(result).IsNull();
    }
}
