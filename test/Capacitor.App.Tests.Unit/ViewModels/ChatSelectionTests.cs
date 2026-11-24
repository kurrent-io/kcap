using Capacitor.App.ProgressiveDisclosure;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class ChatSelectionTests {
    [Test]
    public async Task Copy_preserves_crossed_message_boundaries_with_empty_endpoint_slices() {
        var first = new AssistantTextItem("A") { Key = new(1, 0) };
        var excluded = new AssistantTextItem("") { Key = new(2, 0) };
        var last = new UserTurnItem("B") { Key = new(3, 0) };
        var feed = new object();
        var range = new ChatSelection([first, excluded, last], () => feed);
        var afterFirst = ChatSelection.Point.At(first, "A", 1);
        var beforeLast = ChatSelection.Point.At(last, "B", 0);

        range.Set(afterFirst, beforeLast);
        await Assert.That(range.CopyText()).IsEqualTo("\n");
        range.Set(beforeLast, afterFirst);
        await Assert.That(range.CopyText()).IsEqualTo("\n");
        range.Set(afterFirst with { Offset = 0 }, beforeLast);
        await Assert.That(range.CopyText()).IsEqualTo("A\n");
        range.Set(afterFirst, beforeLast with { Offset = 1 });
        await Assert.That(range.CopyText()).IsEqualTo("\nB");
    }

    [Test]
    public async Task Reversed_ranges_preserve_graphemes_inline_text_and_code_line_endings() {
        var head = new AssistantTextItem("Before **café** and `name`.\n\n```sh\r\n  printf x  \r\n\tprintf y\r\n```") { Key = new(1, 0) };
        var note = new SystemNoteItem("Connection restored.") { SelectionKey = new(2, int.MinValue) };
        var tail = new UserTurnItem("Beta 👩🏽‍💻 after") { Key = new(3, 0) };
        var feed = new object();
        var range = new ChatSelection([head, note, tail], () => feed);
        var headText = range.Read(head);
        var tailText = range.Read(tail);
        var first = ChatSelection.Point.At(head, headText, "Before cafe".Length);
        var last = ChatSelection.Point.At(tail, tailText, "Beta 👩🏽‍💻".Length);
        const string expected = "é and name.\n  printf x  \r\n\tprintf y\nConnection restored.\nBeta 👩🏽‍💻";

        range.Set(last, first);
        await Assert.That(range.CopyText()).IsEqualTo(expected);
        range.Set(first, last);
        await Assert.That(range.CopyText()).IsEqualTo(expected);
        await Assert.That(ChatSelection.Point.At(tail, tailText, "Beta 👩🏽".Length).Offset).IsEqualTo("Beta ".Length);
    }

    [Test]
    public async Task Rechecks_folded_rows_and_cancels_replaced_feed_even_when_keys_match() {
        var first = new AssistantTextItem("First") { Key = new(1, 0) };
        var group = new ToolGroupItem { SelectionKey = new(2, int.MinValue) };
        group.Add(new ToolCallItem("Bash", "private command one", ToolCategory.Command) { Outcome = ToolOutcome.Done });
        group.Add(new ToolCallItem("Bash", "private command two", ToolCategory.Command) { Outcome = ToolOutcome.Done });
        var last = new UserTurnItem("Last") { Key = new(3, 0) };
        var rows = new List<ChatItemViewModel> { first, group, last };
        object feed = new();
        var range = new ChatSelection(rows, () => feed);
        var start = ChatSelection.Point.At(first, "First", 0);
        var end = ChatSelection.Point.At(last, "Last", 4);
        range.Set(start, end);

        group.Toggle();
        await Assert.That(range.CopyText()).Contains("private command one");
        group.Toggle();
        rows.Add(new AssistantTextItem("after selection") { Key = new(4, 0) });
        await Assert.That(range.CopyText()).IsEqualTo($"First\n{group.Summary}\nLast");

        rows[0] = new AssistantTextItem("First") { Key = first.Key };
        feed = new();
        await Assert.That(range.CopyText()).IsNull();
        await Assert.That(range.HasRange).IsFalse();
    }

    [Test]
    public async Task Moving_back_to_a_shell_anchor_keeps_its_text_snapshot() {
        var shell = new ShellCommandItem("! printf shown") { Key = new(1, 0) };
        var reply = new AssistantTextItem("Later reply") { Key = new(2, 0) };
        var feed = new object();
        var range = new ChatSelection([shell, reply], () => feed);
        var anchor = ChatSelection.Point.At(shell, shell.Command, 2);
        range.Set(anchor, anchor);

        await Assert.That(range.Move(1, message: true)).IsTrue();
        shell.Output = "late output";
        await Assert.That(range.Move(1, message: false)).IsTrue();
        await Assert.That(range.Move(-1, message: true)).IsTrue();
        await Assert.That(range.CopyText()).IsEqualTo("p");
        await Assert.That(range.Move(1, message: false)).IsTrue();
        await Assert.That(range.CopyText()).IsEqualTo("pr");
    }

    [Test]
    [NotInParallel]
    public Task Hidden_inline_text_is_omitted_and_a_disclosure_change_invalidates_offsets() => RunOnUiAsync(async () => {
        var before = ProgressiveDisclosureBasis.Current;
        try {
            var row = new AssistantTextItem("First ⌞hidden ⌟word and `code`.") { Key = new(1, 0) };
            var feed = new object();
            var range = new ChatSelection([row], () => feed);
            var shown = range.Read(row);
            range.Set(ChatSelection.Point.At(row, shown, 0), ChatSelection.Point.At(row, shown, shown.Length));
            ProgressiveDisclosureBasis.Set(new(ProgressiveDisclosureDisplay.Hidden, ProgressiveDisclosureDisplay.Hidden));

            await Assert.That(range.CopyText()).IsNull();
            var current = range.Read(row);
            range.Set(ChatSelection.Point.At(row, current, 0), ChatSelection.Point.At(row, current, current.Length));
            await Assert.That(range.CopyText()).IsEqualTo("First word and code.");
        } finally {
            ProgressiveDisclosureBasis.Set(before);
        }
    });
}
