using Capacitor.Models.Transcripts.UserMessages;

namespace Capacitor.Models.Transcripts.Tests.Unit.UserMessages;

/// Which Claude transcript lines are a person's message, and the text each compares by
public class PersonRowsTests {
    static readonly PersonRows Rows = new(TranscriptProjection.For("claude")!, "sess", "claude");

    const string Uuid = "a1b2c3d4-0000-4000-8000-000000000001";

    [Test]
    [Arguments($$$"""{"type":"user","uuid":"{{{Uuid}}}","origin":{"kind":"human"},"message":{"role":"user","content":"Fix the login bug"}}""", "Fix the login bug")]
    [Arguments($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"role":"user","content":[{"type":"text","text":"Fix the login bug"},{"type":"text","text":"and its test"}]}}""", "Fix the login bug\nand its test")]
    [Arguments($$$"""{"type":"attachment","uuid":"{{{Uuid}}}","attachment":{"type":"queued_command","commandMode":"prompt","prompt":"also check the docs"}}""", "also check the docs")]
    [Arguments($$$"""{"type":"user","uuid":"{{{Uuid}}}","message":{"content":"<command-name>/compact</command-name>\n<command-message>compact</command-message>\n<command-args>keep the plan</command-args>"}}""", "/compact keep the plan")]
    [Arguments($$$"""{"type":"system","subtype":"local_command","uuid":"{{{Uuid}}}","commandRun":{"command":"rename","args":"login fix"}}""", "/rename login fix")]
    public async Task Person_rows_come_from_the_projection(string line, string text) =>
        await Assert.That(Rows.Text(line, 3)).IsEqualTo(text);

    [Test]
    [Arguments("""{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""")]
    [Arguments("""{"type":"user","isMeta":true,"message":{"content":"Base directory for this skill: /skills/review"}}""")]
    [Arguments("""{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification><summary>done</summary></task-notification>"}}""")]
    [Arguments("""{"type":"user","isCompactSummary":true,"message":{"content":"This session is being continued from a previous conversation that ran out of context."}}""")]
    [Arguments("""{"type":"assistant","message":{"content":[{"type":"text","text":"Done."}]}}""")]
    [Arguments("""{"type":"user","isSidechain":true,"message":{"content":"Explore the repository"}}""")]
    [Arguments("""{"type":"system","subtype":"local_command","isMeta":true,"commandRun":{"command":"rename","args":"x"}}""")]
    [Arguments("not json")]
    public async Task Every_other_row_gives_no_text(string line) =>
        await Assert.That(Rows.Text(line, 3)).IsNull();

    /// A line answers the same whatever came before it, so a line asked about again after others answers as it did
    [Test]
    public async Task A_line_projects_the_same_whatever_was_asked_before() {
        const string first  = """{"type":"user","uuid":"a1b2c3d4-0000-4000-8000-000000000001","parentUuid":"p","message":{"content":"one"}}""";
        const string second = """{"type":"user","uuid":"a1b2c3d4-0000-4000-8000-000000000002","parentUuid":"p","message":{"content":"two"}}""";

        await Assert.That(Rows.Text(first, 1)).IsEqualTo("one");
        await Assert.That(Rows.Text(second, 2)).IsEqualTo("two");
        await Assert.That(Rows.Text(first, 1)).IsEqualTo("one");
    }

    [Test]
    [Arguments("Fix the login bug", true)]
    [Arguments("/compact keep the plan", true)]
    [Arguments("This session was interrupted by a kcap daemon restart at 2026-11-23 10:00 +01:00. Then continue with where you left off.", true)]
    [Arguments("<task-notification>\n<task-id>b1</task-id>\n<status>completed</status>\n</task-notification>", false)]
    [Arguments("The app was quit while you were working on this", false)]
    public async Task Person_text_is_what_a_persons_row_carrying_it_would_be_classed(string text, bool person) =>
        await Assert.That(PersonRows.IsPersonText(text, "claude")).IsEqualTo(person);
}
