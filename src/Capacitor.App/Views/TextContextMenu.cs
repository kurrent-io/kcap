using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.VisualTree;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// The right-click menu on text: a `kcapPanel` of ghost rows, where Fluent's own is a MenuFlyout
/// in MenuItem chrome. Each flyout is shared the way Fluent shares its own, so a row acts on
/// whichever control opened it.
public static class TextContextMenu {
    /// A row `IsShown` withholds is left out of the menu rather than greyed. A row that starts a group
    /// has a rule above it while rows show on both sides.
    sealed record Row(string Label, Func<Control, bool> CanRun, Action<Control> Run, Func<Control, bool>? IsShown = null, bool StartsGroup = false);

    // The whole message the menu opened on, apart from any selection in it.
    static readonly Row CopyMessage = new("Copy message", _ => true,
        c => _ = MessageRowOf(c)?.CopyMessageAsync(), c => MessageRowOf(c)?.MessageText is not null);

    public static FlyoutBase ForTextBox { get; } = Build<TextBox>(false,
        new("Cut", c => ((TextBox)c).CanCut, c => ((TextBox)c).Cut()),
        new("Copy", c => ((TextBox)c).CanCopy, c => ((TextBox)c).Copy()),
        new("Paste", c => ((TextBox)c).CanPaste, c => ((TextBox)c).Paste()),
        new("Select all", c => !string.IsNullOrEmpty(((TextBox)c).Text), c => ((TextBox)c).SelectAll()));

    public static FlyoutBase ForSelectableTextBlock { get; } = Build<SelectableTextBlock>(false,
        new("Copy", c => ((SelectableTextBlock)c).CanCopy, c => ((SelectableTextBlock)c).Copy()),
        new("Select all", c => !string.IsNullOrEmpty(((SelectableTextBlock)c).Text), c => ((SelectableTextBlock)c).SelectAll()));

    public static FlyoutBase ForMarkdown { get; } = Build<MarkdownView>(true,
        new("Copy image", _ => true, c => _ = ((MarkdownView)c).CopyImageAsync(), c => ((MarkdownView)c).ContextImage is not null),
        new("Copy link", _ => true, c => _ = ((MarkdownView)c).CopyLinkAsync(), c => ((MarkdownView)c).ContextLinkUrl is not null),
        new("Copy selection", c => ((MarkdownView)c).HasSelection, c => _ = ((MarkdownView)c).CopySelectionAsync()),
        CopyMessage,
        new("Select all", _ => true, c => ((MarkdownView)c).SelectAll()),
        new("Bookmark selection", c => ((MarkdownView)c).HasSelection, c => ((MarkdownView)c).BookmarkSelection(),
            c => ((MarkdownView)c).Bookmarks is not null, StartsGroup: true),
        new("Bookmark each list item", _ => true, c => ((MarkdownView)c).BookmarkListItems(),
            c => ((MarkdownView)c).Bookmarks is not null && ((MarkdownView)c).ContextListItems is not null),
        new("Bookmark message", _ => true, c => ((MarkdownView)c).BookmarkMessage(),
            c => ((MarkdownView)c) is { Bookmarks: not null, DataContext: ChatItemViewModel { Bookmark: null } }));

    public static FlyoutBase ForImage { get; } = Build<ToolImagePreview>(true,
        new Row("Copy image", c => ((ToolImagePreview)c).CanCopyImage, c => _ = ((ToolImagePreview)c).CopyImageAsync()), CopyMessage);

    public static FlyoutBase ForMessageRow { get; } = Build<ChatMessageRow>(true, CopyMessage);

    static ChatMessageRow? MessageRowOf(Control target) => target.FindAncestorOfType<ChatMessageRow>(includeSelf: true);

    static Flyout Build<T>(bool tracksMessage, params Row[] rows) where T : Control {
        var flyout = new Flyout();
        flyout.FlyoutPresenterClasses.Add("kcapPanel");
        var panel = new StackPanel { Spacing = 2, Margin = new(6), MinWidth = 160 };
        var buttons = new List<(Button Button, Row Row, Border? Rule)>();
        foreach (var row in rows) {
            Border? rule = null;
            if (row.StartsGroup) {
                rule = new Border { Height = 1, Margin = new(4, 3) };
                rule.Bind(Border.BackgroundProperty, rule.GetResourceObservable("KcapBorderBrush"));
                panel.Children.Add(rule);
            }
            var button = new Button {
                Content = row.Label,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Padding = new(10, 6),
                FontSize = TypeScale.Body,
            };
            button.Classes.Add("kcapGhost");
            button.Click += (_, _) => {
                var target = flyout.Target;
                flyout.Hide();
                if (target is T) row.Run(target);
            };
            panel.Children.Add(button);
            buttons.Add((button, row, rule));
        }
        flyout.Content = panel;
        if (tracksMessage) TrackMessageRow(flyout);
        flyout.Opening += (_, _) => {
            foreach (var (button, row, _) in buttons) {
                button.IsVisible = row.IsShown is null || flyout.Target is T shown && row.IsShown(shown);
                button.IsEnabled = flyout.Target is T target && row.CanRun(target);
            }
            var shownBefore = false;
            for (var i = 0; i < buttons.Count; i++) {
                if (buttons[i].Rule is { } rule) {
                    var shownInGroup = buttons.Skip(i).TakeWhile((entry, n) => n == 0 || entry.Rule is null).Any(entry => entry.Button.IsVisible);
                    rule.IsVisible = shownBefore && shownInGroup;
                }
                shownBefore |= buttons[i].Button.IsVisible;
            }
        };
        return flyout;
    }

    // While the menu is open its message keeps its time, marks and Copy shown.
    static void TrackMessageRow(Flyout flyout) {
        ChatMessageRow? current = null;
        flyout.Opening += (_, _) => {
            current = flyout.Target is { } target ? MessageRowOf(target) : null;
            current?.SetOpenMenu(flyout);
        };
        flyout.Closed += (_, _) => {
            current?.SetOpenMenu(null);
            current = null;
        };
    }
}
