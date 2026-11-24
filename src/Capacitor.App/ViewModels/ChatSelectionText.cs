using System.Globalization;
using System.Text;
using Capacitor.App.GitHubHtml;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Capacitor.App.ViewModels;

/// The selectable transcript text, including rows that have no visual container.
internal static class ChatSelectionText {
    public static string For(ChatItemViewModel row, AgentLinkLabels? labels = null) => row switch {
        UserTurnItem { HasText: true } turn => MarkdownText(turn.Text, ChatPipeline.Prompt, labels),
        AssistantTextItem reply => MarkdownText(reply.Text, ChatPipeline.Instance, labels),
        AssistantThinkingItem { IsExpanded: true } thinking => MarkdownText(thinking.Text, ChatPipeline.Instance, labels),
        AssistantThinkingItem thinking => thinking.HeaderLabel,
        SystemNoteItem { ShowsBody: true } note => MarkdownText(note.Text, ChatPipeline.Instance, labels),
        SystemNoteItem note => note.HeaderLabel,
        ShellCommandItem shell => Join([shell.Command, shell.Output]),
        ToolGroupItem { SuppressedForPendingQuestion: false } group =>
            Join(new[] { group.ShowsSummaryHeader ? group.Summary : group.ShowsKindChip ? group.KindChip : null }
                .Concat(group.VisibleCalls.SelectMany(ToolText))),
        _ => "",
    };

    static string Join(IEnumerable<string?> lines) => string.Join('\n', lines.Where(text => !string.IsNullOrEmpty(text)));

    static IEnumerable<string?> ToolText(ToolCallItem call) {
        if (call.Card is { } card) {
            yield return card.Title;
            yield return card.Name;
            if (card.HasDistinctMeta) yield return card.Meta;
            yield break;
        }
        yield return string.Join(' ', new[] { call.Label, call.HasDetail || !call.HasLabel ? call.LineText : null }
            .Where(text => !string.IsNullOrEmpty(text)));
        if (call.Inspection is not { } inspection) yield break;
        foreach (var section in inspection.Sections) {
            yield return section.Heading;
            yield return section.Text;
            if (section.HasNote) yield return section.Note;
        }
    }

    static string MarkdownText(string source, MarkdownPipeline pipeline, AgentLinkLabels? labels) {
        try {
            var text = new StringBuilder();
            AppendBlocks(Markdown.Parse(source, pipeline), text, labels);
            return text.ToString();
        } catch (ArgumentException e) when (e.Message.Contains("too deeply nested", StringComparison.Ordinal)) {
            return source;
        }
    }

    static void AppendBlocks(ContainerBlock blocks, StringBuilder text, AgentLinkLabels? labels) {
        foreach (var block in blocks) {
            switch (block) {
                case ListBlock list:
                    var number = int.TryParse(list.OrderedStart, CultureInfo.InvariantCulture, out var start) ? start : 1;
                    foreach (var item in list.OfType<ListItemBlock>()) {
                        Separate(text);
                        text.Append(list.IsOrdered ? (number++).ToString(CultureInfo.InvariantCulture) + ". " : "• ");
                        var content = new StringBuilder();
                        AppendBlocks(item, content, labels);
                        text.Append(content);
                    }
                    break;
                case Table table:
                    foreach (var row in table.OfType<TableRow>()) {
                        Separate(text);
                        var cells = new List<string>();
                        foreach (var cell in row.OfType<TableCell>()) {
                            var content = new StringBuilder();
                            AppendBlocks(cell, content, labels);
                            cells.Add(content.ToString());
                        }
                        text.AppendJoin('\t', cells);
                    }
                    break;
                case ContainerBlock container:
                    AppendBlocks(container, text, labels);
                    break;
                case CodeBlock or HtmlBlock:
                    var leaf = (LeafBlock)block;
                    if (leaf.Lines.Count == 0) break;
                    Separate(text);
                    text.Append(leaf.Lines.ToString());
                    break;
                case LeafBlock { Inline: { } inline }:
                    Separate(text);
                    AppendInlines(inline, text, labels);
                    break;
            }
        }
    }

    static void Separate(StringBuilder text) {
        if (text.Length > 0) text.Append('\n');
    }

    static void AppendInlines(ContainerInline container, StringBuilder text, AgentLinkLabels? labels) {
        foreach (var inline in container) {
            switch (inline) {
                case LiteralInline literal: text.Append(literal.Content); break;
                case CodeInline code: text.Append(code.Content); break;
                case LineBreakInline lineBreak:
                    text.Append(!lineBreak.IsHard ? " " : lineBreak.NewLine switch {
                        Markdig.Helpers.NewLine.CarriageReturnLineFeed => "\r\n",
                        Markdig.Helpers.NewLine.CarriageReturn => "\r",
                        _ => "\n",
                    });
                    break;
                case HtmlEntityInline entity: text.Append(entity.Transcoded); break;
                case AutolinkInline link: text.Append(!link.IsEmail ? labels?.For(link.Url)?.Text ?? link.Url : link.Url); break;
                case LinkInline { IsImage: false, Url: { } url } link
                    when (link.IsAutoLink || KcapMarkdownExtension.Label(link) == url) && labels?.For(url) is { } label:
                    text.Append(label.Text);
                    break;
                case HtmlInline html:
                    text.Append(html.Tag.Trim().Replace(" ", "", StringComparison.Ordinal) is var tag
                        && (tag.Equals("<br>", StringComparison.OrdinalIgnoreCase) || tag.Equals("<br/>", StringComparison.OrdinalIgnoreCase))
                        ? "\n" : html.Tag);
                    break;
                case LinkInline { IsImage: true } image:
                    text.Append("![").Append(KcapMarkdownExtension.Label(image)).Append("](").Append(image.Url).Append(')');
                    break;
                case ContainerInline inner: AppendInlines(inner, text, labels); break;
            }
        }
    }
}
