using Markdig;
using Markdig.Extensions.Tables;

namespace Capacitor.App.GitHubHtml;

/// Pipe tables read by GitHub's rules. Markdig's own reading takes a table from a whole paragraph, so one line
/// without a pipe after the last row loses the table and its rows render as one run of text.
public static class GitHubTables {
    public static MarkdownPipelineBuilder UseGitHubTables(this MarkdownPipelineBuilder builder) {
        builder.Extensions.ReplaceOrAdd<PipeTableExtension>(new PipeTableExtension(new PipeTableOptions { UseGfmRules = true }));
        return builder;
    }
}
