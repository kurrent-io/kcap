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
}
