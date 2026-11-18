using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;

namespace Capacitor.App.Views;

internal sealed class MermaidFallback : StackPanel {
    readonly MarkdownView _view;
    readonly Button _open = new() { Content = "Open on GitHub", IsVisible = false };
    readonly List<IDisposable> _bindings = [];

    public MermaidFallback(MarkdownView view, Control code, string source, int position) {
        _view = view;
        Spacing = 8;
        Classes.Add("markdown-mermaid");
        _open.Classes.Add("kcapChip");
        _open.Classes.Add("markdown-mermaid-open");
        _open.Classes.Add("markdown-mermaid-action");
        var copy = CodeBlockActionRenderer.CopyButton(source, "Copy source");
        copy.Classes.Remove("markdown-code-action");
        copy.Classes.Add("kcapChip");
        copy.Classes.Add("markdown-mermaid-copy");
        copy.Classes.Add("markdown-mermaid-action");
        var expanded = view.IsMermaidSourceExpanded(position);
        var toggle = new ToggleButton { Content = expanded ? "Hide source" : "Show source", IsChecked = expanded, Tag = position };
        toggle.Classes.Add("kcapChip");
        toggle.Classes.Add("markdown-mermaid-toggle");
        toggle.Classes.Add("markdown-mermaid-action");
        toggle.Click += (_, _) => view.ToggleMermaidSource(position, toggle.IsChecked == true);
        _open.GotFocus += RevealKeyboardFocus;
        copy.GotFocus += RevealKeyboardFocus;
        toggle.GotFocus += RevealKeyboardFocus;
        var actions = new WrapPanel { LineSpacing = 8, Children = { _open, copy, toggle } };
        actions.Classes.Add("markdown-mermaid-actions");
        var title = new TextBlock { Text = "Mermaid diagram", TextWrapping = TextWrapping.Wrap };
        title.Classes.Add("markdown-mermaid-title");
        var note = new TextBlock {
            Text = "Diagram preview is not available here. You can show or copy the source.",
            TextWrapping = TextWrapping.Wrap,
        };
        note.Classes.Add("markdown-mermaid-note");
        Children.Add(title);
        Children.Add(note);
        Children.Add(actions);
        AddHandler(KeyDownEvent, OnActionKey, RoutingStrategies.Tunnel);
        // MarkView owns selection and its document-wide highlight layer. Keep expanded code
        // in its ordinary document flow; an inner clipped viewport would hide text from its
        // selection index or let selection paint outside that viewport.
        if (expanded) {
            code.Classes.Add("markdown-mermaid-source");
            Children.Add(code);
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _bindings.Add(_open.Bind(Button.CommandParameterProperty,
            new Binding(nameof(MarkdownView.OpenSourceCommandParameter)) { Source = _view }));
        _bindings.Add(_open.Bind(Button.CommandProperty,
            new Binding(nameof(MarkdownView.OpenSourceCommand)) { Source = _view }));
        _bindings.Add(_open.Bind(IsVisibleProperty, new MultiBinding {
            Converter = BoolConverters.And,
            Bindings = {
                new Binding(nameof(MarkdownView.OpenSourceCommand)) { Source = _view, Converter = ObjectConverters.IsNotNull },
                new Binding(nameof(MarkdownView.CanOpenSource)) { Source = _view },
            },
        }));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        foreach (var binding in _bindings) binding.Dispose();
        _bindings.Clear();
        _open.Command = null;
        _open.CommandParameter = null;
        _open.IsVisible = false;
        base.OnDetachedFromVisualTree(e);
    }

    static void RevealKeyboardFocus(object? sender, FocusChangedEventArgs e) {
        if (sender is not Control target || !ReferenceEquals(e.Source, target)
            || e.NavigationMethod is not (NavigationMethod.Tab or NavigationMethod.Directional)) return;
        // ReaderScroll disables general focus scrolling. Only this fallback's keyboard
        // actions opt in, and only when a document viewport actually clips the target.
        var clipped = target.GetVisualAncestors().OfType<ScrollContentPresenter>().Any(viewport =>
            target.TranslatePoint(default, viewport) is { } origin
            && !new Rect(viewport.Bounds.Size).Contains(new Rect(origin, target.Bounds.Size)));
        if (clipped) target.BringIntoView();
    }

    void OnActionKey(object? sender, KeyEventArgs e) {
        if (e.KeyModifiers != KeyModifiers.None || e.Source is not Button { IsFocused: true } target
            || !target.Classes.Contains("markdown-mermaid-action")
            || _view.FindAncestorOfType<ScrollViewer>() is not { } document
            || document.Extent.Height <= document.Viewport.Height) return;
        // MarkView's disabled internal scroller consumes paging keys before the outer reader.
        // An action keeps focus after expansion, so let its paging keys read the real document.
        switch (e.Key) {
            case Key.PageDown: document.PageDown(); break;
            case Key.PageUp: document.PageUp(); break;
            default: return;
        }
        e.Handled = true;
    }
}
