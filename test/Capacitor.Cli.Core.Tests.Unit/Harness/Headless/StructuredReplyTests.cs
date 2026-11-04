using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

public class StructuredReplyTests {
    const string TitleSchema = """{"type":"object","properties":{"title":{"type":"string"}},"required":["title"]}""";

    [Test]
    [Arguments("{\"title\": \"a\"}", """{"title":"a"}""")]
    [Arguments("```json\n{\"title\": \"a\"}\n```", """{"title":"a"}""")]
    [Arguments("  ```\n{\"title\":\"a\"}\n```  ", """{"title":"a"}""")]
    public async Task A_valid_reply_is_returned_reserialised(string reply, string expected) =>
        await Assert.That(StructuredReply.Accept(reply, TitleSchema)).IsEqualTo(expected);

    [Test]
    [Arguments("{}")]
    [Arguments("```json\n{}\n```")]
    [Arguments("Here is the answer: {\"title\":\"a\"}")]
    [Arguments("")]
    [Arguments("{\"title\":")]
    public async Task A_reply_that_fails_parsing_or_the_schema_is_refused(string reply) =>
        await Assert.That(StructuredReply.Accept(reply, TitleSchema) is null).IsTrue();

    [Test]
    public async Task An_unparseable_schema_refuses_every_reply() =>
        await Assert.That(StructuredReply.Accept("{}", "not json") is null).IsTrue();

    [Test]
    public async Task The_instruction_carries_the_schema() {
        var instruction = StructuredReply.SchemaInstruction(TitleSchema);
        await Assert.That(instruction.Contains(TitleSchema, StringComparison.Ordinal)).IsTrue();
        await Assert.That(instruction.Contains("JSON", StringComparison.Ordinal)).IsTrue();
    }
}
