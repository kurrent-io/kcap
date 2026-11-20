using System.Globalization;
using System.Net;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Capacitor.App.GitHubHtml;
using Capacitor.App.ProgressiveDisclosure;
using Capacitor.App.Views;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MarkView.Avalonia;

namespace Capacitor.App.Services;

/// Copies a markdown message as two formats on one clipboard item: the source as plain text, and
/// the rendered HTML that rich editors such as Slack paste with its formatting.
public static class MarkdownClipboard {
    // Raw HTML in a message shows as text in the chat, so the copy escapes it rather than passing markup on.
    static readonly MarkdownPipeline ChatHtml = new MarkdownPipelineBuilder().UseSupportedExtensions().DisableHtml().Build();

    /// Each platform names HTML differently, and Windows alone wraps it in a byte-offset envelope.
    public static DataFormat HtmlFormat { get; } =
        OperatingSystem.IsMacOS() ? DataFormat.CreateStringPlatformFormat("public.html")
        : OperatingSystem.IsWindows() ? DataFormat.CreateBytesPlatformFormat("HTML Format")
        : DataFormat.CreateStringPlatformFormat("text/html");

    public static (string Text, string Html) Content(string markdown, MarkdownFlavor flavor) {
        try {
            if (flavor == MarkdownFlavor.Prompt) return (markdown, Markdown.ToHtml(markdown, ChatPipeline.Prompt));
            var text = flavor == MarkdownFlavor.Chat ? WithoutMarks(markdown) : markdown;
            return (text, Markdown.ToHtml(text, ChatHtml));
        } catch (ArgumentException e) when (e.Message.Contains("too deeply nested", StringComparison.Ordinal)) {
            return (markdown, "<p>" + WebUtility.HtmlEncode(markdown).ReplaceLineEndings("<br />") + "</p>");
        }
    }

    public static DataTransfer Transfer(string markdown, MarkdownFlavor flavor) {
        var (text, html) = Content(markdown, flavor);
        var item = new DataTransferItem();
        item.SetText(text);
        if (HtmlFormat is DataFormat<byte[]> bytes) item.Set(bytes, WindowsEnvelope(html));
        else item.Set((DataFormat<string>)HtmlFormat, html);
        var transfer = new DataTransfer();
        transfer.Add(item);
        return transfer;
    }

    // A platform write can retry after a delay, so an earlier copy left running could land over a later one.
    static Task _lastWrite = Task.CompletedTask;

    public static async Task<bool> CopyAsync(Visual owner, string markdown, MarkdownFlavor flavor) {
        if (TopLevel.GetTopLevel(owner)?.Clipboard is not { } clipboard) return false;
        var transfer = Transfer(markdown, flavor);
        await InOrder(() => clipboard.SetDataAsync(transfer));
        return true;
    }

    /// Starts `write` once every earlier one has finished. Called on the UI thread only.
    internal static Task InOrder(Func<Task> write) {
        var next = AfterAsync(_lastWrite, write);
        _lastWrite = next;
        return next;
    }

    static async Task AfterAsync(Task previous, Func<Task> write) {
        try {
            await previous;
        } catch (Exception) {
            // The earlier click reports its own failure.
        }
        await write();
    }

    /// CF_HTML: a header of byte offsets, which are fixed-width so the header's own length is known before they are.
    public static byte[] WindowsEnvelope(string fragment) {
        const string header = "Version:0.9\r\nStartHTML:{0:D10}\r\nEndHTML:{1:D10}\r\nStartFragment:{2:D10}\r\nEndFragment:{3:D10}\r\n";
        const string before = "<html><body><!--StartFragment-->";
        const string after = "<!--EndFragment--></body></html>";
        var headerLength = Encoding.UTF8.GetByteCount(string.Format(CultureInfo.InvariantCulture, header, 0, 0, 0, 0));
        var startFragment = headerLength + Encoding.UTF8.GetByteCount(before);
        var endFragment = startFragment + Encoding.UTF8.GetByteCount(fragment);
        var endHtml = endFragment + Encoding.UTF8.GetByteCount(after);
        return Encoding.UTF8.GetBytes(string.Format(CultureInfo.InvariantCulture, header, headerLength, endHtml, startFragment, endFragment)
                                      + before + fragment + after);
    }

    /// Only literal text carries marks; one inside code is the code's own text and stays.
    static string WithoutMarks(string markdown) {
        if (!ProgressiveDisclosureMarks.HasMarks(markdown)) return markdown;
        var drop = new HashSet<int>();
        foreach (var literal in Markdown.Parse(markdown, ChatHtml).Descendants<LiteralInline>())
            for (var i = literal.Span.Start; i <= literal.Span.End && i < markdown.Length; i++)
                if (markdown[i] is ProgressiveDisclosureMarks.Open or ProgressiveDisclosureMarks.Close) drop.Add(i);
        if (drop.Count == 0) return markdown;
        var text = new StringBuilder(markdown.Length - drop.Count);
        for (var i = 0; i < markdown.Length; i++)
            if (!drop.Contains(i)) text.Append(markdown[i]);
        return text.ToString();
    }
}
