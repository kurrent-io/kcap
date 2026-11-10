using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// Marks a query's matches in rendered text blocks the way the search pane marks its own: SemiBold
/// in KcapInfoBrush, with the current match also on KcapSelectionBrush. A run a match touches is
/// swapped for pieces split at the match's edges, so the text itself never changes: MarkView's
/// selection index holds each block's plain text, and copying still reads it.
sealed class FindHighlighter {
    readonly List<Split> _splits = [];
    readonly List<Match> _matches = [];
    int _current = -1;

    sealed record Split(InlineCollection Parent, Run Original, Run[] Pieces);

    sealed record Match(TextBlock Block, int Offset, int Length, List<Run> Pieces);

    readonly record struct Leaf(InlineCollection Parent, Run Run, int TextStart, int LayoutStart);

    public int Count => _matches.Count;

    public void Apply(Control root, string? query) {
        Clear();
        if (string.IsNullOrEmpty(query)) return;
        var match = Brush(root, "KcapInfoBrush");
        foreach (var block in root.GetSelfAndLogicalDescendants().OfType<TextBlock>()) {
            if (block.Inlines is not { Count: > 0 } inlines) continue;
            var leaves = new List<Leaf>();
            var text = new StringBuilder();
            var layout = 0;
            Collect(inlines, leaves, text, ref layout);
            var found = TextMatches.Find(text.ToString(), query);
            if (found.Count == 0) continue;
            var first = _matches.Count;
            foreach (var (start, length) in found)
                _matches.Add(new Match(block, LayoutOffset(leaves, start), length, []));
            foreach (var leaf in leaves) SplitLeaf(leaf, found, first, match);
        }
    }

    /// Puts back every run that was split. A block rebuilt since (a code block on a theme switch)
    /// no longer holds the pieces, and is left as it is.
    public void Clear() {
        for (var i = _splits.Count - 1; i >= 0; i--) {
            var split = _splits[i];
            var at = split.Parent.IndexOf(split.Pieces[0]);
            if (at < 0) continue;
            foreach (var piece in split.Pieces) split.Parent.Remove(piece);
            split.Parent.Insert(at, split.Original);
        }
        _splits.Clear();
        _matches.Clear();
        _current = -1;
    }

    /// Forgets the splits without undoing them: the tree they were made in has been thrown away.
    public void Forget() {
        _splits.Clear();
        _matches.Clear();
        _current = -1;
    }

    public void SetCurrent(int index, Control root) {
        if (_current >= 0 && _current < _matches.Count)
            foreach (var piece in _matches[_current].Pieces) piece.ClearValue(TextElement.BackgroundProperty);
        _current = index >= 0 && index < _matches.Count ? index : -1;
        if (_current < 0 || Brush(root, "KcapSelectionBrush") is not { } brush) return;
        foreach (var piece in _matches[_current].Pieces) piece.Background = brush;
    }

    /// The match's first line, in the block's own coordinates.
    public (TextBlock Block, Rect Bounds)? Bounds(int index) {
        if (index < 0 || index >= _matches.Count) return null;
        var match = _matches[index];
        var rects = match.Block.TextLayout.HitTestTextRange(match.Offset, match.Length);
        foreach (var rect in rects) return (match.Block, rect.Translate(new Vector(match.Block.Padding.Left, match.Block.Padding.Top)));
        return (match.Block, new Rect(match.Block.Bounds.Size));
    }

    /// The text matched against, and where each run sits in it and in the block's layout, which
    /// counts a line break as the platform's newline and an embedded control as one character.
    static void Collect(InlineCollection inlines, List<Leaf> leaves, StringBuilder text, ref int layout) {
        foreach (var inline in inlines) {
            switch (inline) {
                case Run run:
                    leaves.Add(new Leaf(inlines, run, text.Length, layout));
                    text.Append(run.Text);
                    layout += run.Text?.Length ?? 0;
                    break;
                case LineBreak:
                    text.Append('\n');
                    layout += Environment.NewLine.Length;
                    break;
                case Span span:
                    Collect(span.Inlines, leaves, text, ref layout);
                    break;
                case InlineUIContainer:
                    text.Append('￼');
                    layout += 1;
                    break;
            }
        }
    }

    static int LayoutOffset(List<Leaf> leaves, int textOffset) {
        foreach (var leaf in leaves) {
            var length = leaf.Run.Text?.Length ?? 0;
            if (textOffset < leaf.TextStart + length) return leaf.LayoutStart + Math.Max(0, textOffset - leaf.TextStart);
        }
        return leaves.Count == 0 ? 0 : leaves[^1].LayoutStart + (leaves[^1].Run.Text?.Length ?? 0);
    }

    void SplitLeaf(Leaf leaf, IReadOnlyList<(int Start, int Length)> found, int firstMatch, IBrush? brush) {
        var text = leaf.Run.Text ?? "";
        var start = leaf.TextStart;
        var end = start + text.Length;
        var pieces = new List<Run>();
        var at = start;
        for (var i = 0; i < found.Count; i++) {
            var (matchStart, matchLength) = found[i];
            var from = Math.Max(matchStart, start);
            var to = Math.Min(matchStart + matchLength, end);
            if (from >= to) continue;
            if (from > at) pieces.Add(Copy(leaf.Run, text[(at - start)..(from - start)]));
            var piece = Copy(leaf.Run, text[(from - start)..(to - start)]);
            piece.FontWeight = FontWeight.SemiBold;
            if (brush is not null) piece.Foreground = brush;
            pieces.Add(piece);
            _matches[firstMatch + i].Pieces.Add(piece);
            at = to;
        }
        if (pieces.Count == 0) return;
        if (at < end) pieces.Add(Copy(leaf.Run, text[(at - start)..]));
        var index = leaf.Parent.IndexOf(leaf.Run);
        if (index < 0) return;
        leaf.Parent.RemoveAt(index);
        leaf.Parent.InsertRange(index, pieces);
        _splits.Add(new Split(leaf.Parent, leaf.Run, [.. pieces]));
    }

    static readonly AvaloniaProperty[] Carried = [
        TextElement.ForegroundProperty, TextElement.BackgroundProperty, TextElement.FontWeightProperty,
        TextElement.FontStyleProperty, TextElement.FontFamilyProperty, TextElement.FontSizeProperty,
        TextElement.FontStretchProperty, TextElement.FontFeaturesProperty, Inline.TextDecorationsProperty,
        Inline.BaselineAlignmentProperty,
    ];

    static Run Copy(Run original, string text) {
        var run = new Run(text);
        run.Classes.AddRange(original.Classes);
        foreach (var property in Carried)
            if (original.IsSet(property)) run.SetValue(property, original.GetValue(property));
        return run;
    }

    static IBrush? Brush(Control root, string key) =>
        root.TryFindResource(key, out var found) || Application.Current?.TryFindResource(key, out found) == true
            ? found as IBrush
            : null;
}
