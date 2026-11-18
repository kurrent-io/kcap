using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.NeedToKnow;

/// Reads the importance marks in an agent's answer — `⌞…⌟` worth knowing, `⌞⌞…⌟⌟` only if curious —
/// and greys or removes what they enclose. Every block starts at must-know, so a span left open ends
/// with its block and a stray close is ignored. Only literal text is scanned: a mark inside code is
/// the code's own text.
public sealed class NeedToKnowMarks : IMarkdownExtension {
    public const char Open = '⌞';
    public const char Close = '⌟';

    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += document => Apply(document, NeedToKnowBasis.Current);

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    public static bool HasMarks(string? text) => text is not null && text.AsSpan().IndexOfAny(Open, Close) >= 0;

    public static void Apply(MarkdownDocument document, NeedToKnowBasis basis) {
        foreach (var leaf in document.Descendants<LeafBlock>().ToArray())
            if (leaf.Inline is { } root && Contains(root)) ApplyTo(leaf, root, basis);
    }

    static bool Contains(ContainerInline root) =>
        root.Descendants<LiteralInline>().Any(literal => literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0);

    static void ApplyTo(LeafBlock leaf, ContainerInline root, NeedToKnowBasis basis) {
        var graded = new List<(Inline Inline, NeedToKnowLevel Level)>();
        var open = new List<NeedToKnowLevel>();
        Walk(root, open, graded);

        var hid = false;
        foreach (var (inline, level) in graded) {
            switch (basis.For(level)) {
                case NeedToKnowDisplay.Hidden:
                    inline.Remove();
                    hid = true;
                    break;
                case NeedToKnowDisplay.Greyed:
                    var grey = new GreyedInline(level);
                    inline.InsertAfter(grey);
                    inline.Remove();
                    grey.AppendChild(inline);
                    break;
            }
        }
        if (!hid) return;
        DropEmptyContainers(root);
        TidySpaces(root);
        if (!HasContent(root)) RemoveBlock(leaf);
    }

    /// Gives every leaf inline the level in force where it sits, splitting literals at their marks.
    static void Walk(ContainerInline container, List<NeedToKnowLevel> open, List<(Inline, NeedToKnowLevel)> graded) {
        foreach (var child in container.ToArray()) {
            switch (child) {
                case LiteralInline literal when literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0:
                    Split(literal, open, graded);
                    break;
                case ContainerInline nested:
                    Walk(nested, open, graded);
                    break;
                default:
                    if (Level(open) is var level and not NeedToKnowLevel.MustKnow) graded.Add((child, level));
                    break;
            }
        }
    }

    /// Markdig's `InsertBefore` leaves the inserted inline without a previous sibling, so a later
    /// `Remove` would cut the chain there; pieces go in after the literal instead.
    static void Split(LiteralInline literal, List<NeedToKnowLevel> open, List<(Inline, NeedToKnowLevel)> graded) {
        var text = literal.Content.ToString();
        Inline last = literal;
        var start = 0;
        for (var i = 0; i < text.Length;) {
            if (text[i] is not (Open or Close)) { i++; continue; }
            last = Emit(last, text[start..i], open, graded);
            var mark = text[i];
            var run = 0;
            while (i < text.Length && text[i] == mark) { run++; i++; }
            if (mark == Open) OpenRun(open, run);
            else CloseRun(open, run);
            start = i;
        }
        Emit(last, text[start..], open, graded);
        literal.Remove();
    }

    static Inline Emit(Inline after, string text, List<NeedToKnowLevel> open, List<(Inline, NeedToKnowLevel)> graded) {
        if (text.Length == 0) return after;
        var piece = new LiteralInline(text);
        after.InsertAfter(piece);
        if (Level(open) is var level and not NeedToKnowLevel.MustKnow) graded.Add((piece, level));
        return piece;
    }

    /// A pair opens only-if-curious and a single corner worth-knowing.
    static void OpenRun(List<NeedToKnowLevel> open, int run) {
        for (; run >= 2; run -= 2) open.Add(NeedToKnowLevel.OnlyIfCurious);
        if (run == 1) open.Add(NeedToKnowLevel.WorthKnowing);
    }

    /// Each close ends the innermost span; a curious span takes two corners when two are there.
    static void CloseRun(List<NeedToKnowLevel> open, int run) {
        while (run > 0 && open.Count > 0) {
            var top = open[^1];
            open.RemoveAt(open.Count - 1);
            run -= top == NeedToKnowLevel.OnlyIfCurious ? Math.Min(2, run) : 1;
        }
    }

    static NeedToKnowLevel Level(List<NeedToKnowLevel> open) => open.Count == 0 ? NeedToKnowLevel.MustKnow : open.Max();

    static void DropEmptyContainers(ContainerInline container) {
        foreach (var nested in container.OfType<ContainerInline>().ToArray()) {
            DropEmptyContainers(nested);
            if (nested.FirstChild is null) nested.Remove();
        }
    }

    /// A hidden span leaves the spaces either side of it; one is kept, and none at the block's edges.
    static void TidySpaces(ContainerInline root) {
        var literals = root.Descendants<LiteralInline>().ToList();
        for (var i = 1; i < literals.Count; i++) {
            var previous = literals[i - 1].Content.ToString();
            var current = literals[i].Content.ToString();
            if (previous.Length > 0 && char.IsWhiteSpace(previous[^1]) && current.Length > 0
                && (char.IsWhiteSpace(current[0]) || current[0] is '.' or ',' or ';' or ':' or '!' or '?'))
                literals[i - 1].Content = new(previous.TrimEnd());
        }
        if (literals.Count == 0) return;
        literals[0].Content = new(literals[0].Content.ToString().TrimStart());
        literals[^1].Content = new(literals[^1].Content.ToString().TrimEnd());
    }

    static bool HasContent(ContainerInline root) =>
        root.Descendants<Inline>().Any(inline => inline switch {
            LiteralInline literal => !literal.Content.IsEmptyOrWhitespace(),
            ContainerInline or LineBreakInline => false,
            _ => true,
        });

    /// A block whose content is all hidden goes, and so does a list item or list it leaves empty.
    /// A table cell keeps its place.
    static void RemoveBlock(Block block) {
        if (block.Parent is not { } parent || parent is TableCell) return;
        parent.Remove(block);
        if (parent is ListItemBlock or ListBlock && parent.Count == 0) RemoveBlock(parent);
    }
}
