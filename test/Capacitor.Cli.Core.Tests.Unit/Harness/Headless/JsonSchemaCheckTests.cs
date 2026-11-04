using System.Text.Json;
using Capacitor.Cli.Core.Harness.Headless;

namespace Capacitor.Cli.Core.Tests.Unit.Harness.Headless;

public class JsonSchemaCheckTests {
    static bool Check(string schema, string instance) {
        using var s = JsonDocument.Parse(schema);
        using var i = JsonDocument.Parse(instance, new JsonDocumentOptions { MaxDepth = 1000 });
        return JsonSchemaCheck.Matches(s.RootElement, i.RootElement);
    }

    static string? Unsupported(string schema) {
        using var s = JsonDocument.Parse(schema);
        return JsonSchemaCheck.UnsupportedKeyword(s.RootElement);
    }

    [Test]
    [Arguments("""{"type":"string"}""", "\"a\"", true)]
    [Arguments("""{"type":"string"}""", "1", false)]
    [Arguments("""{"type":["string","null"]}""", "null", true)]
    [Arguments("""{"type":["string","null"]}""", "true", false)]
    [Arguments("""{"type":"integer"}""", "3", true)]
    [Arguments("""{"type":"integer"}""", "1.0", true)]
    [Arguments("""{"type":"integer"}""", "1.5", false)]
    [Arguments("""{"type":"number"}""", "1.5", true)]
    [Arguments("""{"type":"number"}""", "\"1\"", false)]
    [Arguments("""{"type":"boolean"}""", "false", true)]
    [Arguments("""{"type":"object"}""", "[]", false)]
    [Arguments("""{"type":"array"}""", "[]", true)]
    [Arguments("""{"type":"null"}""", "null", true)]
    [Arguments("""{"properties":{"a":{"type":"integer"}}}""", """{"a":1}""", true)]
    [Arguments("""{"properties":{"a":{"type":"integer"}}}""", """{"a":"x"}""", false)]
    [Arguments("""{"properties":{"a":{"type":"integer"}}}""", """{"b":"x"}""", true)]
    [Arguments("""{"required":["a"]}""", """{"a":1}""", true)]
    [Arguments("""{"required":["a"]}""", "{}", false)]
    [Arguments("""{"properties":{"a":{}},"additionalProperties":false}""", """{"a":1}""", true)]
    [Arguments("""{"properties":{"a":{}},"additionalProperties":false}""", """{"a":1,"b":2}""", false)]
    [Arguments("""{"additionalProperties":{"type":"integer"}}""", """{"a":1}""", true)]
    [Arguments("""{"additionalProperties":{"type":"integer"}}""", """{"a":"x"}""", false)]
    [Arguments("""{"items":{"type":"integer"}}""", "[1,2]", true)]
    [Arguments("""{"items":{"type":"integer"}}""", "[1,\"x\"]", false)]
    [Arguments("""{"enum":["a",1]}""", "1", true)]
    [Arguments("""{"enum":["a",1]}""", "\"b\"", false)]
    [Arguments("""{"const":{"a":[1,2]}}""", """{"a":[1,2]}""", true)]
    [Arguments("""{"const":{"a":[1,2]}}""", """{"a":[1,3]}""", false)]
    [Arguments("""{"const":1}""", "1.0", true)]
    [Arguments("""{"anyOf":[{"type":"string"},{"type":"integer"}]}""", "1", true)]
    [Arguments("""{"anyOf":[{"type":"string"},{"type":"integer"}]}""", "true", false)]
    [Arguments("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""", "1", false)]
    [Arguments("""{"oneOf":[{"type":"number"},{"type":"integer"}]}""", "1.5", true)]
    [Arguments("""{"oneOf":[{"type":"string"},{"type":"integer"}]}""", "true", false)]
    [Arguments("""{"allOf":[{"type":"integer"},{"minimum":3}]}""", "5", true)]
    [Arguments("""{"allOf":[{"type":"integer"},{"minimum":3}]}""", "2", false)]
    [Arguments("""{"minimum":3}""", "3", true)]
    [Arguments("""{"minimum":3}""", "2.9", false)]
    [Arguments("""{"maximum":3}""", "3", true)]
    [Arguments("""{"maximum":3}""", "3.1", false)]
    [Arguments("""{"exclusiveMinimum":3}""", "3", false)]
    [Arguments("""{"exclusiveMinimum":3}""", "3.1", true)]
    [Arguments("""{"exclusiveMaximum":3}""", "3", false)]
    [Arguments("""{"exclusiveMaximum":3}""", "2.9", true)]
    [Arguments("""{"minLength":2}""", "\"ab\"", true)]
    [Arguments("""{"minLength":2}""", "\"a\"", false)]
    [Arguments("""{"maxLength":1}""", "\"\\ud83d\\udc68\\u200d\\ud83d\\udc69\\u200d\\ud83d\\udc67\"", true)]
    [Arguments("""{"maxLength":1}""", "\"ab\"", false)]
    [Arguments("""{"minItems":2}""", "[1,2]", true)]
    [Arguments("""{"minItems":2}""", "[1]", false)]
    [Arguments("""{"maxItems":1}""", "[1]", true)]
    [Arguments("""{"maxItems":1}""", "[1,2]", false)]
    [Arguments("""{"pattern":"^a+$"}""", "\"aaa\"", true)]
    [Arguments("""{"pattern":"^a+$"}""", "\"ab\"", false)]
    [Arguments("""{"pattern":"b"}""", "\"abc\"", true)]
    [Arguments("""{"minLength":5}""", "1", true)]
    [Arguments("""{"description":"x","title":"y","format":"email","default":1}""", "1", true)]
    public async Task A_keyword_accepts_and_refuses(string schema, string instance, bool expected) =>
        await Assert.That(Check(schema, instance)).IsEqualTo(expected);

    [Test]
    [Arguments("#/$defs/n", "$defs")]
    [Arguments("#/definitions/n", "definitions")]
    public async Task A_local_ref_resolves_into_its_container(string reference, string container) {
        var schema = "{\"properties\":{\"a\":{\"$ref\":\"" + reference + "\"}},\"" + container + "\":{\"n\":{\"type\":\"integer\"}}}";
        await Assert.That(Check(schema, """{"a":1}""")).IsTrue();
        await Assert.That(Check(schema, """{"a":"x"}""")).IsFalse();
    }

    [Test]
    public async Task A_root_ref_and_an_escaped_pointer_resolve() {
        var schema = """{"properties":{"a":{"$ref":"#/$defs/a~1b~0c"}},"$defs":{"a/b~c":{"type":"integer"}}}""";
        await Assert.That(Check(schema, """{"a":1}""")).IsTrue();
        await Assert.That(Check(schema, """{"a":"x"}""")).IsFalse();
        await Assert.That(Check("""{"properties":{"a":{"$ref":"#"}},"type":"object"}""", """{"a":{"a":{}}}""")).IsTrue();
    }

    [Test]
    public async Task A_missing_or_non_local_ref_refuses() {
        await Assert.That(Check("""{"$ref":"#/$defs/none"}""", "1")).IsFalse();
        await Assert.That(Check("""{"$ref":"https://x"}""", "1")).IsFalse();
    }

    [Test]
    public async Task Defs_entries_are_not_applied_to_the_instance() {
        await Assert.That(Check("""{"$defs":{"n":{"type":"integer"}},"definitions":{"m":{"type":"integer"}}}""", "\"x\"")).IsTrue();
    }

    [Test]
    public async Task A_self_referencing_schema_validates_nesting_and_stops_at_depth_64() {
        const string schema = """{"$ref":"#/$defs/t","$defs":{"t":{"type":"object","properties":{"c":{"$ref":"#/$defs/t"}}}}}""";
        static string Nest(int levels) => string.Concat(Enumerable.Repeat("{\"c\":", levels)) + "{}" + new string('}', levels);

        await Assert.That(Check(schema, Nest(5))).IsTrue();
        await Assert.That(Check(schema, Nest(200))).IsFalse();
    }

    [Test]
    public async Task A_pattern_that_times_out_does_not_match() {
        var instance = "\"" + new string('a', 60) + "!\"";
        await Assert.That(Check("""{"pattern":"^(a+)+$"}""", instance)).IsFalse();
    }

    [Test]
    [Arguments("""{"if":{}}""", "if")]
    [Arguments("""{"properties":{"a":{"not":{}}}}""", "not")]
    [Arguments("""{"$ref":"https://x"}""", "$ref")]
    [Arguments("""{"properties":{"a":{"$ref":"#/other/x"}}}""", "$ref")]
    [Arguments("""{"items":[{"type":"string"}]}""", "items")]
    [Arguments("""{"uniqueItems":true}""", "uniqueItems")]
    [Arguments("""{"$defs":{"n":{"patternProperties":{}}},"type":"object"}""", "patternProperties")]
    public async Task An_unsupported_keyword_is_named(string schema, string keyword) =>
        await Assert.That(Unsupported(schema)).IsEqualTo(keyword);

    [Test]
    [Arguments("""{"type":"object","properties":{"a":{"type":"string","minLength":1,"description":"d","format":"x"}},"required":["a"],"$schema":"s","$id":"i","$comment":"c","title":"t","default":{},"examples":[1],"readOnly":true,"writeOnly":false,"deprecated":false}""")]
    [Arguments("""{"$ref":"#/$defs/a","$defs":{"a":{"enum":["x"]}},"definitions":{"b":{"const":1}}}""")]
    [Arguments("""{"properties":{"title":{"type":"string"},"if":{"type":"string"}},"required":["if"],"enum":["if"]}""")]
    public async Task Vocabulary_and_annotations_are_supported(string schema) =>
        await Assert.That(Unsupported(schema) is null).IsTrue();
}
