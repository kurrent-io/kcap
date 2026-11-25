using Markdig;
using Markdig.Parsers;
using Markdig.Renderers;
using Markdig.Syntax;

namespace Capacitor.App.GitHubHtml;

public sealed class GitHubHtmlExtension : IMarkdownExtension {
    public void Setup(MarkdownPipelineBuilder pipeline) {
        pipeline.BlockParsers.Replace<HtmlBlockParser>(new GitHubHtmlBlockParser());
        pipeline.DocumentProcessed += Process;
    }

    public void Setup(MarkdownPipeline pipeline, IMarkdownRenderer renderer) { }

    /// The pass runs on the UI thread over text a stranger wrote. A defect in it must not take the
    /// window down: whatever it had not converted renders as source.
    static void Process(MarkdownDocument document) {
        try { GitHubHtmlPass.Run(document); }
        catch (Exception ex) { Console.Error.WriteLine($"kcap: html pass failed: {ex}"); }
    }
}
