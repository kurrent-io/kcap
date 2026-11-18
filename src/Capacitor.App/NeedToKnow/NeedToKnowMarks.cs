using Markdig;
using Markdig.Renderers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.NeedToKnow;

/// Reads the importance marks in an agent's answer — `⌞…⌟` worth knowing, `⌞⌞…⌟⌟` only if curious —
/// and wraps what they enclose for display under the reader's basis. Every block starts at must-know, so a span left open ends
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
            if (leaf.Inline is { } root && Contains(root)) ApplyTo(root, basis);
    }

    static bool Contains(ContainerInline root) =>
        root.Descendants<LiteralInline>().Any(literal => literal.Content.AsSpan().IndexOfAny(Open, Close) >= 0);

    static void ApplyTo(ContainerInline root, NeedToKnowBasis basis) {
        var graded = new List<(Inline Inline, NeedToKnowLevel Level)>();
        Walk(root, [], graded);

        // Neighbouring pieces at one level share a wrapper, so a marked span shows one pair of brackets
        // and a hidden one leaves one mark.
        for (var i = 0; i < graded.Count;) {
            var level = graded[i].Level;
            var end = i + 1;
            while (end < graded.Count && graded[end].Level == level && ReferenceEquals(graded[end].Inline.PreviousSibling, graded[end - 1].Inline)) end++;
            if (basis.For(level) is var display and not NeedToKnowDisplay.Normal) {
                var wrapper = new GradedInline(level, display);
                graded[end - 1].Inline.InsertAfter(wrapper);
                for (var j = i; j < end; j++) {
                    graded[j].Inline.Remove();
                    wrapper.AppendChild(graded[j].Inline);
                }
            }
            i = end;
        }
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
}
