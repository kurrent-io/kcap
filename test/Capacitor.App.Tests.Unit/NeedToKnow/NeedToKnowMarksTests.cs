using System.Text;
using Capacitor.App.NeedToKnow;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;

namespace Capacitor.App.Tests.Unit.NeedToKnow;

public class NeedToKnowMarksTests {
    static readonly MarkdownPipeline WithoutPass = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();

    static readonly NeedToKnowBasis AllGreyed = new(NeedToKnowDisplay.Greyed, NeedToKnowDisplay.Greyed);
    static readonly NeedToKnowBasis AllHidden = new(NeedToKnowDisplay.Hidden, NeedToKnowDisplay.Hidden);
    static readonly NeedToKnowBasis AllNormal = new(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Normal);

    /// One line per leaf block; a graded run shows as `{w:…}` or `{c:…}` when greyed, `[w:…]` when
    /// marked and `&lt;w:…&gt;` when collapsed.
    static string Render(string markdown, NeedToKnowBasis basis) {
        var document = Markdown.Parse(markdown, WithoutPass);
        NeedToKnowMarks.Apply(document, basis);
        return string.Join("\n", document.Descendants<LeafBlock>().Select(leaf => leaf.Inline is { } root ? Text(root) : "<" + leaf.GetType().Name + ">"));
    }

    static string Text(ContainerInline container) {
        var text = new StringBuilder();
        foreach (var inline in container)
            text.Append(inline switch {
                GradedInline graded => Graded(graded),
                LiteralInline lit   => lit.Content.ToString(),
                CodeInline code     => "`" + code.Content + "`",
                EmphasisInline emph => "*" + Text(emph) + "*",
                ContainerInline c   => Text(c),
                LineBreakInline     => "\n",
                _                   => "",
            });
        return text.ToString();
    }

    static string Graded(GradedInline graded) {
        var (open, close) = graded.Display switch {
            NeedToKnowDisplay.Greyed => ("{", "}"),
            NeedToKnowDisplay.Marked => ("[", "]"),
            _                        => ("<", ">"),
        };
        return open + (graded.Level == NeedToKnowLevel.WorthKnowing ? "w:" : "c:") + Text(graded) + close;
    }

    static readonly NeedToKnowBasis AllMarked = new(NeedToKnowDisplay.Marked, NeedToKnowDisplay.Marked);
    static readonly NeedToKnowBasis AllCollapsed = new(NeedToKnowDisplay.Collapsed, NeedToKnowDisplay.Collapsed);

    [Test]
    public async Task A_single_corner_pair_marks_worth_knowing() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllGreyed)).IsEqualTo("Tests pass. {w:7 ran.} CI is green.");

    [Test]
    public async Task A_doubled_corner_pair_marks_only_if_curious() =>
        await Assert.That(Render("Tests pass. ⌞⌞7 ran.⌟⌟ CI is green.", AllGreyed)).IsEqualTo("Tests pass. {c:7 ran.} CI is green.");

    [Test]
    public async Task Normal_display_drops_the_marks_and_keeps_the_text() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllNormal)).IsEqualTo("Tests pass. 7 ran. CI is green.");

    [Test]
    public async Task Each_level_follows_its_own_display() =>
        await Assert.That(Render("A. ⌞B.⌟ ⌞⌞C.⌟⌟ D.", new(NeedToKnowDisplay.Normal, NeedToKnowDisplay.Hidden))).IsEqualTo("A. B. D.");

    [Test]
    public async Task A_collapsed_span_keeps_its_text_for_the_mark_it_leaves() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllCollapsed)).IsEqualTo("Tests pass. <w:7 ran.> CI is green.");

    [Test]
    public async Task A_hidden_span_leaves_one_space_between_its_neighbours() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllHidden)).IsEqualTo("Tests pass. CI is green.");

    [Test]
    public async Task Only_if_curious_is_never_shown_more_than_worth_knowing() =>
        await Assert.That(Render("A. ⌞B.⌟ ⌞⌞C.⌟⌟", new(NeedToKnowDisplay.Greyed, NeedToKnowDisplay.Normal))).IsEqualTo("A. {w:B.} {c:C.}");

    [Test]
    public async Task A_marked_span_is_one_run_however_many_inlines_it_holds() =>
        await Assert.That(Render("A. ⌞Run `x` now.⌟ B.", AllMarked)).IsEqualTo("A. [w:Run `x` now.] B.");

    [Test]
    public async Task A_curious_span_nests_inside_a_worth_span_and_returns_to_it() =>
        await Assert.That(Render("A. ⌞B. ⌞⌞C.⌟⌟ D.⌟ E.", AllGreyed)).IsEqualTo("A. {w:B. }{c:C.}{w: D.} E.");

    [Test]
    public async Task One_run_of_closes_ends_a_curious_span_and_the_worth_span_around_it() =>
        await Assert.That(Render("A. ⌞B. ⌞⌞C.⌟⌟⌟ D.", AllGreyed)).IsEqualTo("A. {w:B. }{c:C.} D.");

    [Test]
    public async Task An_unclosed_span_ends_with_its_block() =>
        await Assert.That(Render("A. ⌞B.\n\nC.", AllGreyed)).IsEqualTo("A. {w:B.}\nC.");

    [Test]
    public async Task A_stray_close_is_ignored() =>
        await Assert.That(Render("A.⌟ B.", AllGreyed)).IsEqualTo("A. B.");

    [Test]
    public async Task Every_bullet_starts_at_must_know() =>
        await Assert.That(Render("- ⌞A.\n- B.", AllGreyed)).IsEqualTo("{w:A.}\nB.");

    [Test]
    public async Task A_span_crosses_emphasis() =>
        await Assert.That(Render("A. ⌞B *c* d.⌟ E.", AllGreyed)).IsEqualTo("A. {w:B }*{w:c}*{w: d.} E.");

    [Test]
    public async Task Marks_inside_code_are_the_code() =>
        await Assert.That(Render("Use `⌞x⌟` here.", AllHidden)).IsEqualTo("Use `⌞x⌟` here.");

    [Test]
    public async Task Code_inside_a_span_is_graded_with_it() =>
        await Assert.That(Render("A. ⌞Run `x`.⌟", AllGreyed)).IsEqualTo("A. {w:Run `x`.}");

    [Test]
    public async Task A_paragraph_collapsed_entirely_keeps_its_place_as_a_mark() =>
        await Assert.That(Render("A.\n\n⌞⌞B.⌟⌟\n\nC.", AllCollapsed)).IsEqualTo("A.\n<c:B.>\nC.");

    [Test]
    public async Task A_paragraph_hidden_entirely_is_removed() =>
        await Assert.That(Render("A.\n\n⌞⌞B.⌟⌟\n\nC.", AllHidden)).IsEqualTo("A.\nC.");

    [Test]
    public async Task A_list_hidden_entirely_is_removed() {
        var document = Markdown.Parse("A.\n\n- ⌞B.⌟\n- ⌞C.⌟", WithoutPass);
        NeedToKnowMarks.Apply(document, AllHidden);
        await Assert.That(document.Descendants<ListBlock>().Any()).IsFalse();
    }

    [Test]
    public async Task Text_without_marks_is_untouched() =>
        await Assert.That(Render("Plain *text* (7/7).", AllHidden)).IsEqualTo("Plain *text* (7/7).");
}
