using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.Views;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.Views.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit.Views;

public class MessageCopyButtonTests {
    static (Window Window, MessageCopyButton Copy) Show(string text, MarkdownFlavor flavor = MarkdownFlavor.Chat) {
        var copy = new MessageCopyButton { Text = text, Flavor = flavor };
        var window = new Window { Content = copy, Width = 200, Height = 100 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
        return (window, copy);
    }

    static Button ButtonOf(MessageCopyButton copy) => copy.GetVisualDescendants().OfType<Button>().Single();

    /// The click puts the message on the clipboard as its source text and as rendered HTML, and the
    /// button confirms the copy in place.
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_click_copies_the_message_as_text_and_html_and_confirms() {
        await RunOnUiAsync(async () => {
            var (window, copy) = Show("1. **one**\n2. two");
            try {
                ClickCentre(window, ButtonOf(copy));
                await copy.PendingCopyForTesting!;
                Dispatcher.UIThread.RunJobs();

                var clipboard = TopLevel.GetTopLevel(copy)!.Clipboard!;
                await Assert.That(await clipboard.TryGetTextAsync()).IsEqualTo("1. **one**\n2. two");
                var html = MarkdownClipboard.HtmlFormat is DataFormat<byte[]> bytes
                    ? System.Text.Encoding.UTF8.GetString(await clipboard.TryGetValueAsync(bytes) ?? [])
                    : await clipboard.TryGetValueAsync((DataFormat<string>)MarkdownClipboard.HtmlFormat);
                await Assert.That(html).Contains("<ol>");
                await Assert.That(html).Contains("<strong>one</strong>");
                await Assert.That(ToolTip.GetTip(ButtonOf(copy))).IsEqualTo("Copied");
            } finally {
                window.Close();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_button_announces_what_it_copies() {
        await RunOnUiAsync(async () => {
            var (window, copy) = Show("hello");
            try {
                await Assert.That(Avalonia.Automation.AutomationProperties.GetName(ButtonOf(copy))).IsEqualTo("Copy message");
            } finally {
                window.Close();
            }
        });
    }
}
