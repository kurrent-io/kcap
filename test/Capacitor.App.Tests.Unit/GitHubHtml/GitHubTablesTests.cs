using Capacitor.App.GitHubHtml;
using Markdig;
using Markdig.Syntax;

namespace Capacitor.App.Tests.Unit.GitHubHtml;

public class GitHubTablesTests {
    const string Table = "| a | b |\n|---|---|\n| 1 | 2 |";
    const string Rows  = "thr(td(p('a')),td(p('b'))),tr(td(p('1')),td(p('2')))";

    static string Chat(string markdown) => TreeDump.Of(Markdown.Parse(markdown, ChatPipeline.Instance));

    [Test]
    public async Task Text_on_the_line_after_the_last_row_is_one_more_row() =>
        await Assert.That(Chat(Table + "\nThe last three are the known gap.")).IsEqualTo($"doc(table2({Rows},tr(td(p('The last three are the known gap.')),td(p()))))");

    /// Read as a setext underline, `---` would turn the whole table into a heading.
    [Test]
    public async Task A_rule_on_the_line_after_the_table_is_a_rule() =>
        await Assert.That(Chat(Table + "\n---\nafter")).IsEqualTo($"doc(table2({Rows}),hr,p('after'))");

    [Test]
    public async Task Indented_code_on_the_line_after_the_table_is_code() =>
        await Assert.That(Chat(Table + "\n    code")).IsEqualTo($"doc(table2({Rows}),CodeBlock())");

    [Test]
    public async Task A_table_in_a_quote_keeps_its_rows_in_the_quote() =>
        await Assert.That(Chat("> " + Table.Replace("\n", "\n> ") + "\n> after")).IsEqualTo($"doc(quote(table2({Rows},tr(td(p('after')),td(p())))))");

    /// An escaped pipe is no column, so the header has one cell against two delimiters and there is no table.
    [Test]
    public async Task A_header_whose_only_pipe_is_escaped_leaves_one_paragraph() {
        var document = Markdown.Parse("a \\| b\n---|---\nafter", ChatPipeline.Instance);

        await Assert.That(document.Count).IsEqualTo(1);
        await Assert.That(document[0]).IsTypeOf<ParagraphBlock>();
    }

    [Test]
    public async Task The_reader_reads_tables_the_same_way() =>
        await Assert.That(Trees.Dump(Table + "\nafter")).IsEqualTo($"doc(table2({Rows},tr(td(p('after')),td(p()))))");
}
