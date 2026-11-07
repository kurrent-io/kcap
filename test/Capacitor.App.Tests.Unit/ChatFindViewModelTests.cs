using Avalonia.Collections;
using Capacitor.App.ViewModels;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit;

public class ChatFindViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Counts_matches_in_rendered_prose_and_skips_tool_calls() => RunOnUiAsync(async () => {
        var items = new AvaloniaList<ChatItemViewModel> {
            new UserTurnItem("Deploy the **deploy** script"),
            new ToolCallItem("Bash", "deploy.sh", ToolCategory.Command),
            new AssistantTextItem("Deployed; see [the deploy log](https://example.com/deploy).\n\n```\nDEPLOY=1\n```"),
        };
        var find = new ChatFindViewModel(items);
        find.Open(0);
        find.Query = "deploy";

        // The link's URL is not rendered text, so its "deploy" is not a match.
        await Assert.That(find.MatchCount).IsEqualTo(5);
        await Assert.That(find.Matches.Select(m => (m.ItemIndex, m.Ordinal)))
            .IsEquivalentTo(new[] { (0, 0), (0, 1), (2, 0), (2, 1), (2, 2) });
        await Assert.That(find.Summary).IsEqualTo("1 of 5");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_new_query_starts_at_the_item_in_view_and_steps_wrap() => RunOnUiAsync(async () => {
        var items = new AvaloniaList<ChatItemViewModel> {
            new AssistantTextItem("alpha"), new AssistantTextItem("beta"), new AssistantTextItem("alpha again"),
        };
        var find = new ChatFindViewModel(items);
        find.Open(1);
        var navigated = new List<ChatFindMatch?>();
        find.Navigated += navigated.Add;
        find.Query = "alpha";

        await Assert.That(find.Current!.ItemIndex).IsEqualTo(2);
        find.Next();
        await Assert.That(find.Current!.ItemIndex).IsEqualTo(0);
        find.Previous();
        await Assert.That(find.Current!.ItemIndex).IsEqualTo(2);
        await Assert.That(navigated.Select(m => m?.ItemIndex)).IsEquivalentTo(new int?[] { 2, 0, 2 });
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task An_append_recounts_without_moving_the_current_match() => RunOnUiAsync(async () => {
        var items = new AvaloniaList<ChatItemViewModel> { new AssistantTextItem("one match"), new AssistantTextItem("two match") };
        var find = new ChatFindViewModel(items);
        find.Open(0);
        find.Query = "match";
        find.Next();
        var navigations = 0;
        find.Navigated += _ => navigations++;

        items.Add(new AssistantTextItem("three match"));

        await Assert.That(find.Summary).IsEqualTo("2 of 3");
        await Assert.That(find.Current!.Item).IsSameReferenceAs(items[1]);
        await Assert.That(navigations).IsEqualTo(0);
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Landing_in_a_folded_block_unfolds_it() => RunOnUiAsync(async () => {
        var thinking = new AssistantThinkingItem("the needle is here");
        thinking.Toggle();
        var items = new AvaloniaList<ChatItemViewModel> { thinking };
        var find = new ChatFindViewModel(items);
        find.Open(0);
        find.Query = "needle";

        await Assert.That(thinking.IsExpanded).IsTrue();
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Closing_clears_the_highlight_query_and_keeps_the_typed_one() => RunOnUiAsync(async () => {
        var find = new ChatFindViewModel(new AvaloniaList<ChatItemViewModel> { new AssistantTextItem("hello") });
        find.Open(0);
        find.Query = "  hello ";
        await Assert.That(find.ActiveQuery).IsEqualTo("hello");

        find.Close();

        await Assert.That(find.ActiveQuery).IsNull();
        await Assert.That(find.MatchCount).IsEqualTo(0);
        await Assert.That(find.Query).IsEqualTo("  hello ");
        await Assert.That(find.Summary).IsEqualTo("");
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task A_restored_find_lands_on_its_match_once_the_transcript_holds_it() => RunOnUiAsync(async () => {
        var items = new AvaloniaList<ChatItemViewModel>();
        var remembered = new List<ChatFindState?>();
        var find = new ChatFindViewModel(items, remembered.Add);
        var navigated = new List<ChatFindMatch?>();
        find.Navigated += navigated.Add;
        find.Restore(new ChatFindState("needle", 2, 0));

        items.AddRange([new AssistantTextItem("a needle"), new AssistantTextItem("plain")]);
        await Assert.That(navigated).IsEmpty();
        await Assert.That(remembered[^1]).IsEqualTo(new ChatFindState("needle", 2, 0));

        items.Add(new AssistantTextItem("the needle we left"));
        await Assert.That(find.Summary).IsEqualTo("2 of 2");
        await Assert.That(navigated.Select(m => m?.ItemIndex)).IsEquivalentTo(new int?[] { 2 });

        find.Close();
        await Assert.That(remembered[^1]).IsNull();
    });
}
