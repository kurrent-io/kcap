using Markdig.Parsers;
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

sealed class GitHubHtmlBlockParser : HtmlBlockParser {
    public override BlockState TryContinue(BlockProcessor processor, Block block) {
        // A task following a disclosure can share its HTML block when there is no blank line.
        if (block is HtmlBlock { Type: HtmlBlockType.InterruptingBlock } html &&
            !processor.IsCodeIndent && IsTaskStart(processor.Line.AsSpan()) && FollowsDetailsClosingBlock(html))
            return BlockState.None;

        return base.TryContinue(processor, block);
    }

    static bool IsTaskStart(ReadOnlySpan<char> line) {
        if (line.Length < 6 || line[0] is not ('-' or '+' or '*') || line[1] is not (' ' or '\t')) return false;
        line = line[2..].TrimStart();
        return line is ['[', ' ' or 'x' or 'X', ']', ' ' or '\t', ..];
    }

    static bool FollowsDetailsClosingBlock(HtmlBlock block) {
        for (var i = block.Lines.Count - 1; i >= 0; i--) {
            var line = block.Lines.Lines[i].Slice.AsSpan().Trim();
            if (line.StartsWith("<!--", StringComparison.Ordinal) &&
                line.IndexOf("-->", StringComparison.Ordinal) == line.Length - 3) continue;
            if (!line.Equals("</details>", StringComparison.OrdinalIgnoreCase)) return false;

            // A closing tag in comment or element content cannot end the Markdown block.
            for (var j = i - 1; j >= 0; j--) {
                var preceding = block.Lines.Lines[j].Slice.AsSpan().Trim();
                if (preceding is not ['<', '/', _, .., '>'] ||
                    preceding[2..^1].IndexOfAny('<', '>') >= 0) return false;
            }
            return true;
        }
        return false;
    }
}
