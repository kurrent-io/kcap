using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The right-click menu on text: a `kcapPanel` of ghost rows, where Fluent's own is a MenuFlyout
/// in MenuItem chrome. Each flyout is shared the way Fluent shares its own, so a row acts on
/// whichever control opened it.
public static class TextContextMenu {
    /// A row `IsShown` withholds is left out of the menu rather than greyed.
    sealed record Row(string Label, Func<Control, bool> CanRun, Action<Control> Run, Func<Control, bool>? IsShown = null);

    public static FlyoutBase ForTextBox { get; } = Build<TextBox>(
        new("Cut", c => ((TextBox)c).CanCut, c => ((TextBox)c).Cut()),
        new("Copy", c => ((TextBox)c).CanCopy, c => ((TextBox)c).Copy()),
        new("Paste", c => ((TextBox)c).CanPaste, c => ((TextBox)c).Paste()),
        new("Select all", c => !string.IsNullOrEmpty(((TextBox)c).Text), c => ((TextBox)c).SelectAll()));

    public static FlyoutBase ForSelectableTextBlock { get; } = Build<SelectableTextBlock>(
        new("Copy", c => ((SelectableTextBlock)c).CanCopy, c => ((SelectableTextBlock)c).Copy()),
        new("Select all", c => !string.IsNullOrEmpty(((SelectableTextBlock)c).Text), c => ((SelectableTextBlock)c).SelectAll()));

    public static FlyoutBase ForMarkdown { get; } = Build<MarkdownView>(
        new("Copy image", _ => true, c => _ = ((MarkdownView)c).CopyImageAsync(), c => ((MarkdownView)c).ContextImage is not null),
        new("Copy link", _ => true, c => _ = ((MarkdownView)c).CopyLinkAsync(), c => ((MarkdownView)c).ContextLinkUrl is not null),
        new("Copy", c => ((MarkdownView)c).HasSelection, c => _ = ((MarkdownView)c).CopySelectionAsync()),
        new("Select all", _ => true, c => ((MarkdownView)c).SelectAll()),
        new("Bookmark selection", c => ((MarkdownView)c).HasSelection, c => ((MarkdownView)c).BookmarkSelection(),
            c => ((MarkdownView)c).Bookmarks is not null),
        new("Bookmark each list item", _ => true, c => ((MarkdownView)c).BookmarkListItems(),
            c => ((MarkdownView)c).Bookmarks is not null && ((MarkdownView)c).ContextListItems is not null),
        new("Bookmark message", _ => true, c => ((MarkdownView)c).BookmarkMessage(),
            c => ((MarkdownView)c) is { Bookmarks: not null, DataContext: ChatItemViewModel { Bookmark: null } }));

    public static FlyoutBase ForImage { get; } = Build<ToolImagePreview>(
        new Row("Copy image", c => ((ToolImagePreview)c).CanCopyImage, c => _ = ((ToolImagePreview)c).CopyImageAsync()));

    static Flyout Build<T>(params Row[] rows) where T : Control {
        var flyout = new Flyout();
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        var panel = new StackPanel { Spacing = 2, Margin = new(6), MinWidth = 160 };
        var buttons = new List<(Button Button, Row Row)>();
        foreach (var row in rows) {
            var button = new Button {
                Content = row.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(10, 6),
                FontSize = 13.5,
            };
            button.Classes.Add("kcapGhost");
            button.Click += (_, _) => {
                var target = flyout.Target;
                flyout.Hide();
                if (target is T) row.Run(target);
            };
            panel.Children.Add(button);
            buttons.Add((button, row));
        }
        flyout.Content = panel;
        flyout.Opening += (_, _) => {
            foreach (var (button, row) in buttons) {
                button.IsVisible = row.IsShown is null || flyout.Target is T shown && row.IsShown(shown);
                button.IsEnabled = flyout.Target is T target && row.CanRun(target);
            }
        };
        return flyout;
    }
}
