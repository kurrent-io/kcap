using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Views;

/// Finds the list a piece of rendered markdown belongs to, by the text of the block it was read from.
public static class MarkdownLists {
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseTaskLists().Build();

    /// The items of the innermost list holding the paragraph that reads <paramref name="blockText"/>, each
    /// as its own text without the lists nested in it; null when that paragraph is in no list.
    /// <paramref name="occurrence"/> counts the paragraphs before it that read the same, so the same text
    /// in two lists finds the one clicked.
    public static IReadOnlyList<string>? ItemsAround(string? markdown, string? blockText, int occurrence = 0) {
        if (string.IsNullOrWhiteSpace(markdown) || Normalize(blockText) is not { Length: > 0 } hit) return null;
        MarkdownDocument document;
        try { document = Markdown.Parse(markdown, Pipeline); }
        catch (Exception) { return null; }
        // Counted as the viewer counts its blocks, code blocks included.
        var same = document.Descendants<LeafBlock>().Where(leaf => Normalize(TextOf(leaf)) == hit).ToList();
        if (same.Count == 0) return null;
        var paragraph = same[Math.Min(occurrence, same.Count - 1)];
        return paragraph.Parent is ListItemBlock { Parent: ListBlock list }
            ? list.OfType<ListItemBlock>().Select(TextOf).Where(static item => item.Length > 0).ToList()
            : null;
    }

    static string TextOf(LeafBlock leaf) => leaf switch {
        CodeBlock code => code.Lines.ToString(),
        { Inline: { } inline } => Plain(inline),
        _ => "",
    };

    static string TextOf(ListItemBlock item) =>
        Normalize(string.Join(' ', item.OfType<LeafBlock>().Select(TextOf))) ?? "";

    static string Plain(ContainerInline container) =>
        string.Concat(container.Select(static inline => inline switch {
            TaskList => "",
            LiteralInline literal => literal.Content.ToString(),
            CodeInline code => code.Content,
            AutolinkInline link => link.Url,
            LineBreakInline => " ",
            ContainerInline inner => Plain(inner),
            _ => "",
        }));

    static string? Normalize(string? text) =>
        text is null ? null : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
