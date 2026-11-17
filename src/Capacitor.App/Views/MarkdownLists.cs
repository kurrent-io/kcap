using Markdig;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.Views;

/// Finds the list a piece of rendered markdown belongs to, by the text of the block it was read from.
public static class MarkdownLists {
    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseTaskLists().Build();

    /// The items of the innermost list holding an item whose text is <paramref name="blockText"/>, each
    /// as its own text without the lists nested in it; null when no list holds that text.
    public static IReadOnlyList<string>? ItemsAround(string? markdown, string? blockText) {
        if (string.IsNullOrWhiteSpace(markdown) || Normalize(blockText) is not { Length: > 0 } hit) return null;
        MarkdownDocument document;
        try { document = Markdown.Parse(markdown, Pipeline); }
        catch (Exception) { return null; }
        return document.Descendants<ListBlock>()
            .Select(list => (List: list, Items: list.OfType<ListItemBlock>().Select(TextOf).ToList()))
            .Where(found => found.Items.Any(item => item.Length > 0 && (item == hit || hit.EndsWith(item, StringComparison.Ordinal))))
            .OrderByDescending(found => Depth(found.List))
            .Select(found => found.Items.Where(static item => item.Length > 0).ToList())
            .FirstOrDefault();
    }

    static int Depth(Block block) {
        var depth = 0;
        for (var parent = block.Parent; parent is not null; parent = parent.Parent) depth++;
        return depth;
    }

    static string TextOf(ListItemBlock item) =>
        Normalize(string.Join(' ', item.OfType<LeafBlock>().Select(static leaf => leaf.Inline is { } inline ? Plain(inline) : ""))) ?? "";

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
