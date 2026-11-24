using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

// A parked message keeps its template and attached styles, but carries no item.
sealed class ChatMessageContainer : Decorator {
    public ChatMessageContainer(IDataTemplate template, ChatMessageRow row) {
        Template = template;
        Child = row;
    }

    public IDataTemplate Template { get; }

    public void ResetMessage(ChatTabView? view) {
        if (Child?.GetVisualDescendants().OfType<MarkdownView>().FirstOrDefault() is { } markdown) {
            view?.ReleaseRecycledMarkdown(markdown);
            markdown.ResetMessageState();
        }
        DataContext = null;
    }
}
