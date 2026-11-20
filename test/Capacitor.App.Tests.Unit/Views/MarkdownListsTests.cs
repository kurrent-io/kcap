using Capacitor.App.Views;

namespace Capacitor.App.Tests.Unit.Views;

/// A right-click on a list item finds the list it sits in, from the text of the rendered block.
public class MarkdownListsTests {
    const string Review = """
        Points to cover:

        1. Lock **order**
        2. Retry `jitter`
           - back off from 50ms
           - cap at 2s
        3. Logging

        - [ ] Write the test
        """;

    [Test]
    public async Task An_item_reads_the_items_of_its_own_list_without_their_nested_lists() =>
        await Assert.That(MarkdownLists.ItemsAround(Review, "Retry jitter")).IsEquivalentTo(["Lock order", "Retry jitter", "Logging"]);

    [Test]
    public async Task A_nested_item_reads_the_innermost_list() =>
        await Assert.That(MarkdownLists.ItemsAround(Review, "cap at 2s")).IsEquivalentTo(["back off from 50ms", "cap at 2s"]);

    [Test]
    public async Task A_task_list_item_reads_without_its_box() =>
        await Assert.That(MarkdownLists.ItemsAround(Review, "Write the test")).IsEquivalentTo(["Write the test"]);

    [Test]
    public async Task Text_outside_any_list_reads_no_list() =>
        await Assert.That(MarkdownLists.ItemsAround(Review, "Points to cover:")).IsNull();

    [Test]
    public async Task A_paragraph_ending_in_an_items_text_reads_no_list() =>
        await Assert.That(MarkdownLists.ItemsAround("- Logging\n\nNot Logging", "Not Logging")).IsNull();

    /// The same item text in two lists: which occurrence was clicked picks the list.
    [Test]
    public async Task The_clicked_occurrence_of_an_items_text_reads_its_own_list() {
        const string twice = "- Retry\n- Lock order\n\nThen:\n\n- Retry\n- Logging";
        await Assert.That(MarkdownLists.ItemsAround(twice, "Retry")).IsEquivalentTo(["Retry", "Lock order"]);
        await Assert.That(MarkdownLists.ItemsAround(twice, "Retry", occurrence: 1)).IsEquivalentTo(["Retry", "Logging"]);
    }

    [Test]
    public async Task Any_paragraph_of_a_loose_item_reads_its_list() =>
        await Assert.That(MarkdownLists.ItemsAround("- Retry\n\n  with jitter\n\n- Logging", "with jitter"))
            .IsEquivalentTo(["Retry with jitter", "Logging"]);

    /// A code block reading the same as an item counts towards the occurrence, as the viewer counts it.
    [Test]
    public async Task A_code_block_reading_the_same_counts_as_an_occurrence() =>
        await Assert.That(MarkdownLists.ItemsAround(CodeThenTwoLists, "Retry", occurrence: 1))
            .IsEquivalentTo(["Retry", "Lock order"]);

    internal const string CodeThenTwoLists = "```\nRetry\n```\n\n- Retry\n- Lock order\n\nThen:\n\n- Retry\n- Logging";

    [Test]
    public async Task An_item_that_is_a_code_block_reads_its_code() =>
        await Assert.That(MarkdownLists.ItemsAround("- ```\n  Retry\n  ```\n- Logging", "Retry")).IsEquivalentTo(["Retry", "Logging"]);
}
