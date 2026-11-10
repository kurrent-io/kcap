using System.Text;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.ViewModels;

/// The text of each block a markdown body renders as, so a count made here agrees with the matches
/// the rendered view highlights: a match never spans two blocks there, and a soft break shows as a space.
public static class MarkdownFindText {
    public static IReadOnlyList<string> Blocks(string? markdown, MarkdownPipeline pipeline) {
        if (string.IsNullOrWhiteSpace(markdown)) return [];
        MarkdownDocument document;
        try {
            document = Markdown.Parse(markdown, pipeline);
        } catch (ArgumentException) {
            // Nested past Markdig's depth limit: the view shows such text as plain source.
            return [markdown];
        }
        var blocks = new List<string>();
        var text = new StringBuilder();
        foreach (var block in document.Descendants<LeafBlock>()) {
            if (block is CodeBlock code) {
                blocks.Add(code.Lines.ToString());
                continue;
            }
            if (block.Inline is not { } inline) continue;
            text.Clear();
            Append(inline, text);
            if (text.Length > 0) blocks.Add(text.ToString());
        }
        return blocks;
    }

    static void Append(ContainerInline container, StringBuilder text) {
        foreach (var inline in container) {
            switch (inline) {
                case LiteralInline literal: text.Append(literal.Content); break;
                case CodeInline code: text.Append(code.Content); break;
                case LineBreakInline lineBreak: text.Append(lineBreak.IsHard ? '\n' : ' '); break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded); break;
                case AutolinkInline link: text.Append(link.Url); break;
                case HtmlInline html: text.Append(html.Tag); break;
                case ContainerInline inner: Append(inner, text); break;
            }
        }
    }
}
