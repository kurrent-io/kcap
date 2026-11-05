using Capacitor.Models.Transcripts.Harness.Cursor;

namespace Capacitor.Models.Transcripts.Tests.Unit.Harness.Cursor;

/// <summary>
/// Cursor wraps every composer user turn in a prompt envelope:
///   &lt;timestamp&gt;Wednesday, Jul 8, 2026, 9:48 AM (UTC-4)&lt;/timestamp&gt;
///   &lt;user_query&gt;
///   {the query the user typed}
///   &lt;/user_query&gt;
/// Older/subagent turns carry only the &lt;user_query&gt; block. These tests pin
/// <see cref="CursorUserQuery.Parse"/>, which unwraps the anchored envelope so
/// canonical content is the clean query, surfaces the embedded send time, and
/// never throws.
/// </summary>
public class CursorUserQueryTests {
    [Test]
    public async Task Full_envelope_unwraps_query_and_parses_timestamp() {
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:49 AM (UTC-4)</timestamp>\n" +
            "<user_query>\nHave we worked on ACP before?\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("Have we worked on ACP before?");
        await Assert.That(parsed.Timestamp).IsNotNull();
        // 9:49 AM UTC-4 == 13:49 UTC.
        await Assert.That(parsed.Timestamp!.Value.ToUniversalTime())
            .IsEqualTo(new DateTimeOffset(2026, 7, 8, 13, 49, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Envelope_without_timestamp_unwraps_query_and_yields_null_timestamp() {
        const string raw = "<user_query>\nGet to know this repo\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("Get to know this repo");
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Multiline_query_body_internal_newlines_are_preserved() {
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 10:04 AM (UTC-4)</timestamp>\n" +
            "<user_query>\nLine one\n\nLine three\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("Line one\n\nLine three");
    }

    [Test]
    public async Task User_authored_whitespace_is_preserved_only_wrapper_newline_stripped() {
        // Only Cursor's single wrapper newline each side is removed; the user's own
        // leading/trailing spaces survive so the query still equals the hook prompt
        // used as the attachments-FIFO key.
        const string raw = "<user_query>\n  indented and spaced  \n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("  indented and spaced  ");
    }

    [Test]
    public async Task Plain_text_without_envelope_is_returned_unchanged() {
        const string raw = "just a normal prompt with no wrapper";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo(raw);
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Tag_in_the_middle_of_a_prompt_is_not_unwrapped() {
        // The tag is not the leading envelope boundary — must pass through verbatim.
        const string raw = "Please explain <user_query>foo</user_query> in XML";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo(raw);
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Query_open_at_start_but_trailing_content_is_not_unwrapped() {
        // Leading <user_query> but the block does not span to the end → not the envelope.
        const string raw = "<user_query>foo</user_query> then more text";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo(raw);
    }

    [Test]
    public async Task Whitespace_padded_tag_is_not_treated_as_envelope() {
        // Cursor never pads the envelope, so a prompt that merely contains the tag with
        // surrounding whitespace must pass through byte-for-byte (FIFO-key fidelity).
        await Assert.That(CursorUserQuery.Parse("  <user_query>foo</user_query>").Query)
            .IsEqualTo("  <user_query>foo</user_query>");
        await Assert.That(CursorUserQuery.Parse("<user_query>foo</user_query> ").Query)
            .IsEqualTo("<user_query>foo</user_query> ");
    }

    [Test]
    public async Task Trailing_garbage_after_utc_offset_yields_null_timestamp() {
        // The (UTC…) suffix must be the last token; trailing garbage means the value is
        // not Cursor's timestamp format, so unwrap the query but return no timestamp.
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:00 AM (UTC+5:30) garbage</timestamp>\n" +
            "<user_query>\nhi\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("hi");
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Padded_separator_after_timestamp_is_not_treated_as_envelope() {
        // Cursor writes exactly one newline between </timestamp> and <user_query>. A
        // padded separator is not its shape, so the whole turn must pass through verbatim.
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:00 AM (UTC-4)</timestamp>  <user_query>x</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo(raw);
        await Assert.That(parsed.Timestamp).IsNull();
    }

    /// <summary>A prompt Cursor writes for itself leaves a blank line after the timestamp.</summary>
    [Test]
    public async Task A_blank_line_after_the_timestamp_still_unwraps() {
        const string raw =
            "<timestamp>Monday, Sep 14, 2026, 3:10 PM (UTC+6)</timestamp>\n\n<user_query>Briefly inform the user about the task result.</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("Briefly inform the user about the task result.");
        await Assert.That(parsed.Timestamp).IsNotNull();
    }

    /// <summary>An image ask leads with an [Image] line per image and the saved-files list.</summary>
    [Test]
    public async Task An_image_ask_unwraps_to_its_query() {
        const string raw = "[Image]\n[Image]\n<image_files>\nThe following images were provided by the user and saved to disk for future use:\n"
                         + "1. /u/.cursor/assets/image-0.jpg\n2. /u/.cursor/assets/image-1.jpg\n</image_files>\n"
                         + "<timestamp>Monday, Sep 14, 2026, 5:22 PM (UTC+6)</timestamp>\n<user_query>\n[Image #1]  here spacing seems too tight?\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("[Image #1]  here spacing seems too tight?");
        await Assert.That(parsed.Timestamp).IsNotNull();
    }

    [Test]
    public async Task Image_lines_written_with_crlf_unwrap() {
        const string raw = "[Image]\r\n<image_files>\r\n1. /u/a.jpg\r\n</image_files>\r\n<user_query>\r\nlook\r\n</user_query>";

        await Assert.That(CursorUserQuery.Parse(raw).Query).IsEqualTo("look");
    }

    [Test]
    public async Task Image_lines_without_the_saved_files_list_are_not_unwrapped() {
        const string raw = "[Image]\n<user_query>x</user_query>";

        await Assert.That(CursorUserQuery.Parse(raw).Query).IsEqualTo(raw);
    }

    [Test]
    public async Task Unsigned_utc_offset_yields_null_timestamp() {
        // Cursor emits "(UTC)" or signed offsets; an unsigned "(UTC5)" is malformed and
        // must degrade to a null timestamp while the query still unwraps.
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:00 AM (UTC5)</timestamp>\n" +
            "<user_query>\nhi\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("hi");
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Timestamp_without_newline_separator_is_not_treated_as_envelope() {
        // Cursor always writes a single newline between </timestamp> and <user_query>;
        // its absence means this is not the envelope shape → pass through, null timestamp.
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:00 AM (UTC-4)</timestamp><user_query>\nhi\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo(raw);
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Empty_envelope_yields_empty_query() {
        // Attachment-only turn: Cursor emits an empty user_query block.
        const string raw = "<user_query>\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("");
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Utc_zero_offset_parses_to_utc() {
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:00 AM (UTC)</timestamp>\n" +
            "<user_query>\nhi\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Timestamp!.Value.ToUniversalTime())
            .IsEqualTo(new DateTimeOffset(2026, 7, 8, 9, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Half_hour_positive_offset_parses() {
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 12:30 PM (UTC+5:30)</timestamp>\n" +
            "<user_query>\nhi\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        // 12:30 PM +5:30 == 07:00 UTC.
        await Assert.That(parsed.Timestamp!.Value.ToUniversalTime())
            .IsEqualTo(new DateTimeOffset(2026, 7, 8, 7, 0, 0, TimeSpan.Zero));
    }

    [Test]
    public async Task Out_of_range_offset_yields_null_timestamp_without_throwing() {
        // (UTC+15) is a syntactic match but outside DateTimeOffset's ±14:00 range —
        // must degrade to a null timestamp, never throw and abort the line.
        const string raw =
            "<timestamp>Wednesday, Jul 8, 2026, 9:00 AM (UTC+15)</timestamp>\n" +
            "<user_query>\nstill want the query\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("still want the query");
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Unparseable_timestamp_still_unwraps_query() {
        const string raw =
            "<timestamp>not a real date</timestamp>\n" +
            "<user_query>\nstill want the query\n</user_query>";

        var parsed = CursorUserQuery.Parse(raw);

        await Assert.That(parsed.Query).IsEqualTo("still want the query");
        await Assert.That(parsed.Timestamp).IsNull();
    }

    [Test]
    public async Task Empty_or_null_input_is_safe() {
        await Assert.That(CursorUserQuery.Parse("").Query).IsEqualTo("");
        await Assert.That(CursorUserQuery.Parse(null).Query).IsEqualTo("");
    }
}
