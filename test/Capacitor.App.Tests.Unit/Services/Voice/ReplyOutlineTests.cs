using Capacitor.App.Services.Voice;

namespace Capacitor.App.Tests.Unit.Services.Voice;

public class ReplyOutlineTests {
    const string Titled = """
        I fixed the redirect loop and added tests. Details below.

        ## Summary
        The login page redirected to itself when the session cookie had expired, because the guard read the stale cookie before refreshing it.

        ## Changes
        - `AuthGuard` now refreshes the token before reading the cookie.
        - The refresh call is retried once on a network error.
        - Removed the duplicate redirect in `LoginPage`.

        ## Tests
        Added three tests covering an expired cookie, a refresh failure and a valid session. All forty-two tests pass locally.

        ## Next steps
        Deploy to staging and watch the login error rate for a day before rolling out further.
        """;

    [Test]
    public async Task A_short_reply_is_read_whole() {
        var outline = ReplyOutline.From("Done. **All tests pass.**");

        await Assert.That(outline.Opening()).IsEqualTo("Done. All tests pass.");
        await Assert.That(outline.HasMore).IsFalse();
    }

    [Test]
    public async Task A_long_reply_opens_with_its_first_sentence_and_its_parts() {
        var opening = ReplyOutline.From(Titled).Opening();

        await Assert.That(opening).IsEqualTo(
            "I fixed the redirect loop and added tests. The reply has 4 parts: Summary, Changes, Tests and Next steps. " +
            "Say read more, name a part, or read it all.");
    }

    [Test]
    public async Task Read_more_walks_the_parts_in_order_then_says_it_is_done() {
        var outline = ReplyOutline.From(Titled);
        outline.Opening();

        await Assert.That(outline.Next()).StartsWith("Summary. The login page redirected");
        await Assert.That(outline.Next()).StartsWith("Changes. AuthGuard now refreshes");
        await Assert.That(outline.Next()).StartsWith("Tests.");
        await Assert.That(outline.Next()).EndsWith("That's the end.");
        await Assert.That(outline.Next()).IsNull();
    }

    [Test]
    [Arguments("tests", "Tests.")]
    [Arguments("the next steps part", "Next steps.")]
    [Arguments("two", "Changes.")]
    [Arguments("last", "Next steps.")]
    public async Task A_part_is_found_by_its_heading_or_its_number(string query, string start) =>
        await Assert.That(ReplyOutline.From(Titled).Read(query)).StartsWith(start);

    [Test]
    public async Task Peeking_at_the_next_part_does_not_use_it_up() {
        var outline = ReplyOutline.From(Titled);
        outline.Opening();

        var peeked = outline.PeekNext();

        await Assert.That(peeked).StartsWith("Summary.");
        await Assert.That(outline.Next()).IsEqualTo(peeked);
        await Assert.That(outline.PeekNext()).StartsWith("Changes.");
    }

    [Test]
    public async Task Reading_a_part_moves_read_more_past_it() {
        var outline = ReplyOutline.From(Titled);
        outline.Read("changes");
        await Assert.That(outline.Next()).StartsWith("Tests.");
    }

    [Test]
    public async Task Bold_lines_count_as_headings() {
        var reply = "Here's the plan.\n\n**Backend:**\n" + new string('a', 10) + " " + Sentences(12) +
                    "\n\n**Frontend:**\n" + Sentences(12);
        await Assert.That(ReplyOutline.From(reply).Opening()).Contains("2 parts: Backend and Frontend");
    }

    [Test]
    public async Task A_long_reply_without_headings_is_read_in_paragraph_parts_without_repeating_its_opening() {
        var reply = string.Join("\n\n", Enumerable.Range(1, 6).Select(i => $"Paragraph {i} starts here. " + Sentences(6)));
        var outline = ReplyOutline.From(reply);

        var opening = outline.Opening();
        await Assert.That(opening).StartsWith("Paragraph 1 starts here. It's a long reply, in");
        await Assert.That(outline.Next()).DoesNotContain("Paragraph 1 starts here.");
    }

    static string Sentences(int count) => string.Join(" ", Enumerable.Repeat("This sentence fills the reply out a little.", count));
}
