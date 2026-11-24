using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;

namespace Capacitor.App.ProgressiveDisclosure;

/// Reads the importance marks in an agent's answer — `⌞…⌟` detail, `⌞⌞…⌟⌟` footnote —
/// and applies the reader's basis to what they enclose: a hidden span is removed and a faded one wrapped in a
/// <see cref="GradedInline"/>. A span closed in a later block grades every block up to its close;
/// one never closed ends with its own block, and a stray close is ignored. Only literal text is
/// scanned: a mark inside code is the code's own text.
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
        var leaves = Leaves(document).ToArray();
        var spans = new Spans(Pair(leaves));
        for (spans.Block = 0; spans.Block < leaves.Length; spans.Block++) {
            var leaf = leaves[spans.Block];
            if (leaf.Inline is { } root) {
                if (spans.Active || Contains(root)) ApplyTo(leaf, root, basis, spans);
            } else if (spans.Current is { } span && basis.For(span.Level) == ProgressiveDisclosureDisplay.Hidden) {
                RemoveBlock(leaf);
            }
        }
    }

    static bool Contains(ContainerInline root) =>
        root.Descendants<LiteralInline>().Any(literal => literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0);

    /// Leaf blocks in reading order.
    static IEnumerable<LeafBlock> Leaves(ContainerBlock container) {
        foreach (var block in container.ToArray())
            switch (block) {
                case LeafBlock leaf: yield return leaf; break;
                case ContainerBlock nested:
                    foreach (var inner in Leaves(nested)) yield return inner;
                    break;
            }
    }

    /// Which opens, numbered in reading order, are closed anywhere in the document.
    static List<bool> Pair(IEnumerable<LeafBlock> leaves) {
        var closed = new List<bool>();
        var open = new List<(int Id, ProgressiveDisclosureLevel Level)>();
        foreach (var leaf in leaves) {
            if (leaf.Inline is not { } root) continue;
            foreach (var literal in Literals(root))
                foreach (var (mark, run) in Runs(literal.Content.ToString())) {
                    if (mark == Open) {
                        foreach (var level in Opened(run)) {
                            open.Add((closed.Count, level));
                            closed.Add(false);
                        }
                        continue;
                    }
                    for (var left = run; left > 0 && open.Count > 0;) {
                        var (id, level) = open[^1];
                        open.RemoveAt(open.Count - 1);
                        closed[id] = true;
                        left -= Width(level, left);
                    }
                }
        }
        return closed;
    }

    /// The literals the walk splits, in the same order.
    static IEnumerable<LiteralInline> Literals(ContainerInline container) {
        foreach (var child in container)
            switch (child) {
                case LiteralInline literal: yield return literal; break;
                case ContainerInline nested and not LinkInline { IsImage: true }:
                    foreach (var inner in Literals(nested)) yield return inner;
                    break;
            }
    }

    static IEnumerable<(char Mark, int Run)> Runs(string text) {
        for (var i = 0; i < text.Length;) {
            if (text[i] is not (Open or Close)) { i++; continue; }
            var mark = text[i];
            var run = 0;
            while (i < text.Length && text[i] == mark) { run++; i++; }
            yield return (mark, run);
        }
    }

    /// A pair opens footnote and a single corner detail.
    static IEnumerable<ProgressiveDisclosureLevel> Opened(int run) {
        for (; run >= 2; run -= 2) yield return ProgressiveDisclosureLevel.Footnote;
        if (run == 1) yield return ProgressiveDisclosureLevel.Detail;
    }

    /// How many corners of a close run a span takes: a footnote takes two when two are there.
    static int Width(ProgressiveDisclosureLevel level, int run) => level == ProgressiveDisclosureLevel.Footnote ? Math.Min(2, run) : 1;

    static void ApplyTo(LeafBlock leaf, ContainerInline root, ProgressiveDisclosureBasis basis, Spans spans) {
        var graded = new List<(Inline Inline, Span Span)>();
        Walk(root, spans, graded);

        // Neighbouring pieces of one span share a wrapper, so a faded span lights as one run.
        var removed = false;
        for (var i = 0; i < graded.Count;) {
            var span = graded[i].Span;
            var end = i + 1;
            while (end < graded.Count && graded[end].Span == span && ReferenceEquals(graded[end].Inline.PreviousSibling, graded[end - 1].Inline)) end++;
            var display = basis.For(span.Level);
            if (display == ProgressiveDisclosureDisplay.Hidden) {
                for (var j = i; j < end; j++) graded[j].Inline.Remove();
                removed = true;
            } else if (display == ProgressiveDisclosureDisplay.Faded) {
                var wrapper = new GradedInline(span.Level, span);
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

    /// One opened span. Its identity groups the faded runs it leaves in each block.
    sealed class Span(ProgressiveDisclosureLevel level, int id, int block) {
        public ProgressiveDisclosureLevel Level { get; } = level;
        public int Id { get; } = id;
        public int Block { get; } = block;
    }

    /// The spans open at the walk's position across the whole document. A span never closed is
    /// open only in the block it started in.
    sealed class Spans(List<bool> closed) {
        readonly List<Span> _open = [];
        int _next;

        public int Block { get; set; }

        public bool Active => _open.Any(IsActive);

        /// The innermost span at the highest level in force, or none at must-know.
        public Span? Current {
            get {
                Span? current = null;
                foreach (var span in _open)
                    if (IsActive(span) && (current is null || span.Level >= current.Level)) current = span;
                return current;
            }
        }

        bool IsActive(Span span) => span.Block == Block || closed[span.Id];

        public void OpenRun(int run) {
            foreach (var level in Opened(run)) _open.Add(new(level, _next++, Block));
        }

        /// Each close ends the innermost span.
        public void CloseRun(int run) {
            while (run > 0 && _open.Count > 0) {
                var top = _open[^1];
                _open.RemoveAt(_open.Count - 1);
                run -= Width(top.Level, run);
            }
        }
    }

    /// Gives every leaf inline the span in force where it sits, splitting literals at their marks.
    static void Walk(ContainerInline container, Spans spans, List<(Inline, Span)> graded) {
        foreach (var child in container.ToArray()) {
            switch (child) {
                case LiteralInline literal when literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0:
                    Split(literal, spans, graded);
                    break;
                // An image is graded whole: its alt text is not shown, so it cannot be split.
                case ContainerInline nested and not LinkInline { IsImage: true }:
                    Walk(nested, spans, graded);
                    break;
                default:
                    if (spans.Current is { } span) graded.Add((child, span));
                    break;
            }
        }
    }

    /// Markdig's `InsertBefore` leaves the inserted inline without a previous sibling, so a later
    /// `Remove` would cut the chain there; pieces go in after the literal instead.
    static void Split(LiteralInline literal, Spans spans, List<(Inline, Span)> graded) {
        var text = literal.Content.ToString();
        Inline last = literal;
        var start = 0;
        for (var i = 0; i < text.Length;) {
            if (text[i] is not (Open or Close)) { i++; continue; }
            last = Emit(last, text[start..i], spans, graded);
            var mark = text[i];
            var run = 0;
            while (i < text.Length && text[i] == mark) { run++; i++; }
            if (mark == Open) spans.OpenRun(run);
            else spans.CloseRun(run);
            start = i;
        }
        Emit(last, text[start..], spans, graded);
        literal.Remove();
    }

    static Inline Emit(Inline after, string text, Spans spans, List<(Inline, Span)> graded) {
        if (text.Length == 0) return after;
        var piece = new LiteralInline(text);
        after.InsertAfter(piece);
        if (spans.Current is { } span) graded.Add((piece, span));
        return piece;
    }

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
        var leaves = root.Descendants<Inline>().Where(static inline => inline is not ContainerInline or LinkInline { IsImage: true }).ToList();
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
