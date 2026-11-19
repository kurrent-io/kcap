using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Threading;

namespace Capacitor.App.Views.Pages;

/// The frame every list page draws over the main area: heading, count, a search box that takes focus when the page
/// opens with any filters beside it, the list (Content) in a scrolling column, a message under it, and a footer.
/// Its look is the ListPage theme.
public sealed class ListPage : ContentControl {
    public static readonly StyledProperty<string> HeadingProperty = AvaloniaProperty.Register<ListPage, string>(nameof(Heading), "");
    public static readonly StyledProperty<string> CountProperty = AvaloniaProperty.Register<ListPage, string>(nameof(Count), "");
    public static readonly StyledProperty<string> SearchProperty =
        AvaloniaProperty.Register<ListPage, string>(nameof(Search), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> SearchPlaceholderProperty =
        AvaloniaProperty.Register<ListPage, string>(nameof(SearchPlaceholder), "Search");
    public static readonly StyledProperty<object?> FiltersProperty = AvaloniaProperty.Register<ListPage, object?>(nameof(Filters));
    public static readonly StyledProperty<string?> MessageProperty = AvaloniaProperty.Register<ListPage, string?>(nameof(Message));
    public static readonly StyledProperty<object?> FooterProperty = AvaloniaProperty.Register<ListPage, object?>(nameof(Footer));

    public string Heading { get => GetValue(HeadingProperty); set => SetValue(HeadingProperty, value); }
    public string Count { get => GetValue(CountProperty); set => SetValue(CountProperty, value); }
    public string Search { get => GetValue(SearchProperty); set => SetValue(SearchProperty, value); }
    public string SearchPlaceholder { get => GetValue(SearchPlaceholderProperty); set => SetValue(SearchPlaceholderProperty, value); }
    public object? Filters { get => GetValue(FiltersProperty); set => SetValue(FiltersProperty, value); }
    public string? Message { get => GetValue(MessageProperty); set => SetValue(MessageProperty, value); }
    public object? Footer { get => GetValue(FooterProperty); set => SetValue(FooterProperty, value); }

    protected override void OnApplyTemplate(TemplateAppliedEventArgs e) {
        base.OnApplyTemplate(e);
        if (e.NameScope.Find<TextBox>("PART_Search") is { } search)
            Dispatcher.UIThread.Post(() => search.Focus(), DispatcherPriority.Loaded);
    }
}
