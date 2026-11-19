using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;

namespace Capacitor.App.ProgressiveDisclosure;

/// Reads the importance marks in an agent's answer — `⌞…⌟` detail, `⌞⌞…⌟⌟` footnote —
/// and applies the reader's basis to what they enclose: a hidden span is removed and a faded one wrapped in a
/// <see cref="GradedInline"/>. Every block starts at must-know, so a span left open ends
/// with its block and a stray close is ignored. Only literal text is scanned: a mark inside code is
/// the code's own text.
public sealed class ProgressiveDisclosureMarks : IMarkdownExtension {
    public const char Open = '⌞';
    public const char Close = '⌟';

    public void Setup(MarkdownPipelineBuilder pipeline) => pipeline.DocumentProcessed += document => Apply(document, ProgressiveDisclosureBasis.Current);

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    public static bool HasMarks(string? text) => text is not null && text.AsSpan().IndexOfAny(Open, Close) >= 0;

    static readonly MarkdownPipeline Plain = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();

    /// Whether the text grades anything: a mark outside code, which only a parse can tell. Text nested past
    /// Markdig's depth limit shows as plain source, so it grades nothing.
    public static bool Grades(string? text) {
        if (!HasMarks(text)) return false;
        try {
            return Markdown.Parse(text!, Plain).Descendants<LeafBlock>().Any(leaf => leaf.Inline is { } root && Contains(root));
        } catch (ArgumentException e) when (e.Message.Contains("too deeply nested", StringComparison.Ordinal)) {
            return false;
        }
    }

    public static void Apply(MarkdownDocument document, ProgressiveDisclosureBasis basis) {
        foreach (var leaf in document.Descendants<LeafBlock>().ToArray())
            if (leaf.Inline is { } root && Contains(root)) ApplyTo(leaf, root, basis);
    }

    static bool Contains(ContainerInline root) =>
        root.Descendants<LiteralInline>().Any(literal => literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0);

    static void ApplyTo(LeafBlock leaf, ContainerInline root, ProgressiveDisclosureBasis basis) {
        var graded = new List<(Inline Inline, ProgressiveDisclosureLevel Level)>();
        Walk(root, [], graded);

        // Neighbouring pieces at one level share a wrapper, so a faded span lights as one run.
        var removed = false;
        for (var i = 0; i < graded.Count;) {
            var level = graded[i].Level;
            var end = i + 1;
            while (end < graded.Count && graded[end].Level == level && ReferenceEquals(graded[end].Inline.PreviousSibling, graded[end - 1].Inline)) end++;
            var display = basis.For(level);
            if (display == ProgressiveDisclosureDisplay.Hidden) {
                for (var j = i; j < end; j++) graded[j].Inline.Remove();
                removed = true;
            } else if (display == ProgressiveDisclosureDisplay.Faded) {
                var wrapper = new GradedInline(level);
                graded[end - 1].Inline.InsertAfter(wrapper);
                for (var j = i; j < end; j++) {
                    graded[j].Inline.Remove();
                    wrapper.AppendChild(graded[j].Inline);
                }
            }
            i = end;
        }
        if (!removed) return;
        DropEmptyContainers(root);
        TidySpaces(root);
        if (!HasContent(root)) RemoveBlock(leaf);
    }

    /// Gives every leaf inline the level in force where it sits, splitting literals at their marks.
    static void Walk(ContainerInline container, List<ProgressiveDisclosureLevel> open, List<(Inline, ProgressiveDisclosureLevel)> graded) {
        foreach (var child in container.ToArray()) {
            switch (child) {
                case LiteralInline literal when literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0:
                    Split(literal, open, graded);
                    break;
                // An image is graded whole: its alt text is not shown, so it cannot be split.
                case ContainerInline nested and not LinkInline { IsImage: true }:
                    Walk(nested, open, graded);
                    break;
                default:
                    if (Level(open) is var level and not ProgressiveDisclosureLevel.MustKnow) graded.Add((child, level));
                    break;
            }
        }
    }

    /// Markdig's `InsertBefore` leaves the inserted inline without a previous sibling, so a later
    /// `Remove` would cut the chain there; pieces go in after the literal instead.
    static void Split(LiteralInline literal, List<ProgressiveDisclosureLevel> open, List<(Inline, ProgressiveDisclosureLevel)> graded) {
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

    static Inline Emit(Inline after, string text, List<ProgressiveDisclosureLevel> open, List<(Inline, ProgressiveDisclosureLevel)> graded) {
        if (text.Length == 0) return after;
        var piece = new LiteralInline(text);
        after.InsertAfter(piece);
        if (Level(open) is var level and not ProgressiveDisclosureLevel.MustKnow) graded.Add((piece, level));
        return piece;
    }

    /// A pair opens footnote and a single corner detail.
    static void OpenRun(List<ProgressiveDisclosureLevel> open, int run) {
        for (; run >= 2; run -= 2) open.Add(ProgressiveDisclosureLevel.Footnote);
        if (run == 1) open.Add(ProgressiveDisclosureLevel.Detail);
    }

    /// Each close ends the innermost span; a footnote span takes two corners when two are there.
    static void CloseRun(List<ProgressiveDisclosureLevel> open, int run) {
        while (run > 0 && open.Count > 0) {
            var top = open[^1];
            open.RemoveAt(open.Count - 1);
            run -= top == ProgressiveDisclosureLevel.Footnote ? Math.Min(2, run) : 1;
        }
    }

    static ProgressiveDisclosureLevel Level(List<ProgressiveDisclosureLevel> open) => open.Count == 0 ? ProgressiveDisclosureLevel.MustKnow : open.Max();

    static void DropEmptyContainers(ContainerInline container) {
        foreach (var nested in container.OfType<ContainerInline>().ToArray()) {
            DropEmptyContainers(nested);
            // An image with no alt text has no children either.
            if (nested.FirstChild is null && nested is not LinkInline { IsImage: true }) nested.Remove();
        }
    }

    /// A hidden span leaves the spaces either side of it; one is kept, and none at the block's edges.
    /// Only text that renders side by side is compared, so the space before inline code stays.
    static void TidySpaces(ContainerInline root) {
        var leaves = root.Descendants<Inline>().Where(static inline => inline is not ContainerInline || inline is LinkInline { IsImage: true }).ToList();
        for (var i = 1; i < leaves.Count; i++) {
            if (leaves[i - 1] is not LiteralInline before || leaves[i] is not LiteralInline after) continue;
            var previous = before.Content.ToString();
            var current = after.Content.ToString();
            if (previous.Length > 0 && char.IsWhiteSpace(previous[^1]) && current.Length > 0
                && (char.IsWhiteSpace(current[0]) || current[0] is '.' or ',' or ';' or ':' or '!' or '?'))
                before.Content = new(previous.TrimEnd());
        }
        if (leaves is [LiteralInline first, ..]) first.Content = new(first.Content.ToString().TrimStart());
        if (leaves is [.., LiteralInline last]) last.Content = new(last.Content.ToString().TrimEnd());
    }

    static bool HasContent(ContainerInline root) =>
        root.Descendants<Inline>().Any(inline => inline switch {
            LiteralInline literal => !literal.Content.IsEmptyOrWhitespace(),
            LinkInline { IsImage: true } => true,
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
