using System.Text;
using Capacitor.App.ProgressiveDisclosure;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;

namespace Capacitor.App.Tests.Unit.ProgressiveDisclosure;

public class ProgressiveDisclosureMarksTests {
    static readonly MarkdownPipeline WithoutPass = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();

    static readonly ProgressiveDisclosureBasis AllFaded = new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Faded);
    static readonly ProgressiveDisclosureBasis AllHidden = new(ProgressiveDisclosureDisplay.Hidden, ProgressiveDisclosureDisplay.Hidden);
    static readonly ProgressiveDisclosureBasis AllNormal = new(ProgressiveDisclosureDisplay.Normal, ProgressiveDisclosureDisplay.Normal);

    /// One line per leaf block; a faded run shows as `{w:…}` or `{c:…}`.
    static string Render(string markdown, ProgressiveDisclosureBasis basis) {
        var document = Markdown.Parse(markdown, WithoutPass);
        ProgressiveDisclosureMarks.Apply(document, basis);
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

    static string Graded(GradedInline graded) =>
        "{" + (graded.Level == ProgressiveDisclosureLevel.Detail ? "w:" : "c:") + Text(graded) + "}";

    [Test]
    [Arguments("Plain text.", false)]
    [Arguments("Use `⌞x⌟` here.", false)]
    [Arguments("```\n⌞x⌟\n```", false)]
    [Arguments("A. ⌞B.⌟", true)]
    [Arguments("A. ⌞⌞B.", true)]
    public async Task Only_a_mark_outside_code_grades_a_text(string markdown, bool grades) =>
        await Assert.That(ProgressiveDisclosureMarks.Grades(markdown)).IsEqualTo(grades);

    [Test]
    public async Task A_single_corner_pair_marks_detail() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllFaded)).IsEqualTo("Tests pass. {w:7 ran.} CI is green.");

    [Test]
    public async Task A_doubled_corner_pair_marks_footnote() =>
        await Assert.That(Render("Tests pass. ⌞⌞7 ran.⌟⌟ CI is green.", AllFaded)).IsEqualTo("Tests pass. {c:7 ran.} CI is green.");

    [Test]
    public async Task Normal_display_drops_the_marks_and_keeps_the_text() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllNormal)).IsEqualTo("Tests pass. 7 ran. CI is green.");

    [Test]
    public async Task Each_level_follows_its_own_display() =>
        await Assert.That(Render("A. ⌞B.⌟ ⌞⌞C.⌟⌟ D.", new(ProgressiveDisclosureDisplay.Normal, ProgressiveDisclosureDisplay.Hidden))).IsEqualTo("A. B. D.");

    [Test]
    public async Task A_hidden_span_leaves_one_space_between_its_neighbours() =>
        await Assert.That(Render("Tests pass. ⌞7 ran.⌟ CI is green.", AllHidden)).IsEqualTo("Tests pass. CI is green.");

    [Test]
    public async Task Footnote_is_never_shown_more_than_detail() =>
        await Assert.That(Render("A. ⌞B.⌟ ⌞⌞C.⌟⌟", new(ProgressiveDisclosureDisplay.Faded, ProgressiveDisclosureDisplay.Normal))).IsEqualTo("A. {w:B.} {c:C.}");

    [Test]
    public async Task A_graded_span_is_one_run_however_many_inlines_it_holds() =>
        await Assert.That(Render("A. ⌞Run `x` now.⌟ B.", AllFaded)).IsEqualTo("A. {w:Run `x` now.} B.");

    [Test]
    public async Task A_footnote_span_nests_inside_a_detail_span_and_returns_to_it() =>
        await Assert.That(Render("A. ⌞B. ⌞⌞C.⌟⌟ D.⌟ E.", AllFaded)).IsEqualTo("A. {w:B. }{c:C.}{w: D.} E.");

    [Test]
    public async Task One_run_of_closes_ends_a_footnote_span_and_the_detail_span_around_it() =>
        await Assert.That(Render("A. ⌞B. ⌞⌞C.⌟⌟⌟ D.", AllFaded)).IsEqualTo("A. {w:B. }{c:C.} D.");

    [Test]
    public async Task An_unclosed_span_ends_with_its_block() =>
        await Assert.That(Render("A. ⌞B.\n\nC.", AllFaded)).IsEqualTo("A. {w:B.}\nC.");

    [Test]
    public async Task A_stray_close_is_ignored() =>
        await Assert.That(Render("A.⌟ B.", AllFaded)).IsEqualTo("A. B.");

    [Test]
    public async Task Every_bullet_starts_at_must_know() =>
        await Assert.That(Render("- ⌞A.\n- B.", AllFaded)).IsEqualTo("{w:A.}\nB.");

    [Test]
    public async Task A_span_crosses_emphasis() =>
        await Assert.That(Render("A. ⌞B *c* d.⌟ E.", AllFaded)).IsEqualTo("A. {w:B }*{w:c}*{w: d.} E.");

    [Test]
    public async Task Marks_inside_code_are_the_code() =>
        await Assert.That(Render("Use `⌞x⌟` here.", AllHidden)).IsEqualTo("Use `⌞x⌟` here.");

    [Test]
    public async Task Code_inside_a_span_is_graded_with_it() =>
        await Assert.That(Render("A. ⌞Run `x`.⌟", AllFaded)).IsEqualTo("A. {w:Run `x`.}");

    [Test]
    public async Task A_paragraph_hidden_entirely_is_removed() =>
        await Assert.That(Render("A.\n\n⌞⌞B.⌟⌟\n\nC.", AllHidden)).IsEqualTo("A.\nC.");

    [Test]
    public async Task A_list_hidden_entirely_is_removed() {
        var document = Markdown.Parse("A.\n\n- ⌞B.⌟\n- ⌞C.⌟", WithoutPass);
        ProgressiveDisclosureMarks.Apply(document, AllHidden);
        await Assert.That(document.Descendants<ListBlock>().Any()).IsFalse();
    }

    [Test]
    public async Task A_hidden_span_keeps_the_space_before_inline_code() =>
        await Assert.That(Render("Use `git status`. ⌞Extra.⌟", AllHidden)).IsEqualTo("Use `git status`.");

    [Test]
    public async Task A_hidden_span_keeps_the_space_after_leading_inline_code() =>
        await Assert.That(Render("`x` then ⌞y.⌟ z.", AllHidden)).IsEqualTo("`x` then z.");

    [Test]
    public async Task A_hidden_span_keeps_an_image_without_alt_text() {
        var document = Markdown.Parse("![](diagram.png) ⌞Optional.⌟", WithoutPass);
        ProgressiveDisclosureMarks.Apply(document, AllHidden);
        await Assert.That(document.Descendants<LinkInline>().Any(link => link.IsImage)).IsTrue();
    }

    [Test]
    public async Task A_hidden_span_removes_an_image_inside_it() {
        var document = Markdown.Parse("Lead. ⌞![](diagram.png) Optional.⌟", WithoutPass);
        ProgressiveDisclosureMarks.Apply(document, AllHidden);
        await Assert.That(document.Descendants<LinkInline>().Any(link => link.IsImage)).IsFalse();
    }

    [Test]
    public async Task Text_nested_past_the_parser_limit_grades_nothing() =>
        await Assert.That(ProgressiveDisclosureMarks.Grades(new string('>', 200) + " A. ⌞B.⌟")).IsFalse();

    [Test]
    public async Task Text_without_marks_is_untouched() =>
        await Assert.That(Render("Plain *text* (7/7).", AllHidden)).IsEqualTo("Plain *text* (7/7).");
}
