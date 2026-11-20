using System.Text;
using Avalonia.Input;
using Capacitor.App.Services;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.Services;

public class MarkdownClipboardTests {
    static string Html(string markdown, MarkdownFlavor flavor = MarkdownFlavor.Chat) => MarkdownClipboard.Content(markdown, flavor).Html;

    static string Text(string markdown, MarkdownFlavor flavor = MarkdownFlavor.Chat) => MarkdownClipboard.Content(markdown, flavor).Text;

    /// Slack keeps only what the HTML carries, so every structure the chat draws has to be in it.
    [Test]
    public async Task Html_carries_lists_emphasis_and_paragraph_breaks() {
        var html = Html("First paragraph with **bold** and *em*.\n\nSecond paragraph.\n\n1. one\n2. two\n\n- a\n- b\n");

        await Assert.That(html).Contains("<strong>bold</strong>");
        await Assert.That(html).Contains("<em>em</em>");
        await Assert.That(html).Contains("<p>Second paragraph.</p>");
        await Assert.That(html).Contains("<ol>");
        await Assert.That(html).Contains("<li>one</li>");
        await Assert.That(html).Contains("<ul>");
        await Assert.That(html).Contains("<li>a</li>");
    }

    [Test]
    public async Task Text_is_the_markdown_source() {
        const string markdown = "1. one\n2. **two**\n\nAfter.";

        await Assert.That(Text(markdown)).IsEqualTo(markdown);
    }

    /// Raw HTML in a message is shown as text in the chat, so the copy must not turn it into markup.
    [Test]
    public async Task Raw_html_in_the_message_is_escaped() {
        var html = Html("before <img src=x onerror=alert(1)> after");

        await Assert.That(html).DoesNotContain("<img");
        await Assert.That(html).Contains("&lt;img");
    }

    /// The marks are reading aids the chat interprets; a copy carries everything they enclose, without them.
    [Test]
    public async Task Disclosure_marks_are_dropped_and_what_they_enclose_kept() {
        var content = MarkdownClipboard.Content("Core point. ⌞Some detail.⌟ ⌞⌞A footnote.⌟⌟", MarkdownFlavor.Chat);

        await Assert.That(content.Text).IsEqualTo("Core point. Some detail. A footnote.");
        await Assert.That(content.Html).DoesNotContain("⌞");
        await Assert.That(content.Html).Contains("Some detail.");
        await Assert.That(content.Html).Contains("A footnote.");
    }

    /// A mark inside code is the code's own text, as the chat shows it.
    [Test]
    public async Task A_mark_inside_code_is_kept() {
        const string markdown = "Use `⌞x⌟` here.\n\n```\n⌞y⌟\n```";

        var content = MarkdownClipboard.Content(markdown, MarkdownFlavor.Chat);

        await Assert.That(content.Text).IsEqualTo(markdown);
        await Assert.That(content.Html).Contains("<code>⌞x⌟</code>");
    }

    /// What a person typed keeps each newline as a line break and a backslash as a literal, as the chat shows it.
    [Test]
    public async Task A_prompt_keeps_its_line_breaks_and_backslashes() {
        var html = Html("line one\nline two in C:\\Users\\me\\.config", MarkdownFlavor.Prompt);

        await Assert.That(html).Contains("line one<br />");
        await Assert.That(html).Contains("C:\\Users\\me\\.config");
    }

    [Test]
    public async Task Text_nested_past_the_parser_limit_is_copied_as_escaped_text() {
        var deep = new string('>', 200) + " deep";

        var content = MarkdownClipboard.Content(deep, MarkdownFlavor.Chat);

        await Assert.That(content.Text).IsEqualTo(deep);
        await Assert.That(content.Html).Contains("&gt;&gt;&gt;");
    }

    /// Windows reads HTML only inside its CF_HTML envelope, whose byte offsets must point at the fragment.
    [Test]
    public async Task The_windows_envelope_offsets_frame_the_fragment() {
        const string fragment = "<p>héllo <strong>wörld</strong></p>";

        var bytes = MarkdownClipboard.WindowsEnvelope(fragment);
        var envelope = Encoding.UTF8.GetString(bytes);

        int Offset(string key) {
            var line = envelope.Split("\r\n").First(l => l.StartsWith(key + ":", StringComparison.Ordinal));
            return int.Parse(line[(key.Length + 1)..], System.Globalization.CultureInfo.InvariantCulture);
        }
        var start = Offset("StartFragment");
        var end = Offset("EndFragment");
        await Assert.That(Encoding.UTF8.GetString(bytes, start, end - start)).IsEqualTo(fragment);
        await Assert.That(Encoding.UTF8.GetString(bytes, Offset("StartHTML"), Offset("EndHTML") - Offset("StartHTML"))).StartsWith("<html>");
        await Assert.That(Offset("EndHTML")).IsEqualTo(bytes.Length);
    }

    /// A later copy waits for an earlier write still in flight, so the earlier one cannot land over it;
    /// a failed earlier write does not stop the later one.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_write_starts_only_after_the_earlier_one_finishes() {
        await RunOnUiAsync(async () => {
            var first = new TaskCompletionSource();
            var order = new List<string>();
            var earlier = MarkdownClipboard.InOrder(async () => { await first.Task; order.Add("first"); throw new InvalidOperationException("retry failed"); });
            var later = MarkdownClipboard.InOrder(() => { order.Add("second"); return Task.CompletedTask; });

            await Task.Yield();
            await Assert.That(order).IsEmpty();

            first.SetResult();
            await later;
            await Assert.That(order).IsEquivalentTo(["first", "second"], TUnit.Assertions.Enums.CollectionOrdering.Matching);
            await Assert.That(earlier.IsFaulted).IsTrue();
        });
    }

    [Test]
    public async Task The_transfer_carries_text_and_html_on_one_item() {
        using var transfer = MarkdownClipboard.Transfer("**hi**", MarkdownFlavor.Chat);

        var item = transfer.Items.Single();
        await Assert.That(item.TryGetRaw(DataFormat.Text)).IsEqualTo("**hi**");
        await Assert.That(item.Formats.Count).IsEqualTo(2);
        await Assert.That(item.Formats.Any(f => f == MarkdownClipboard.HtmlFormat)).IsTrue();
    }
}
