using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;

namespace Capacitor.App.Views;

/// A GitHub pull request or issue link in chat, drawn as a chip in its line. A press opens the link
/// through the view's link command, as a link's does; its state follows the view's
/// <see cref="MarkdownView.GitHubLinksProperty"/> while the chip is shown.
public partial class GitHubLinkChip : UserControl {
    /// The line takes an embedded control's whole height as its ascent; measured this much shorter,
    /// the chip hangs into the descent the line already has and its text sits on the line's baseline.
    const double Hang = 5;

    IDisposable? _watch;
    bool _pressed;

    public GitHubLinkChip() : this(new GitHubLink("", GitHubLinkKind.PullRequest, "", "", 0)) { }

    public GitHubLinkChip(GitHubLink link) {
        Link = link;
        InitializeComponent();
        Margin = new Thickness(0, 0, 0, -Hang);
        DataContext = new GitHubLinkChipViewModel(link, null);
    }

    public GitHubLink Link { get; }

    public string Url => Link.Url;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        _watch = this.FindAncestorOfType<MarkdownView>()?.GetValue(MarkdownView.GitHubLinksProperty)?.Watch(Link)
            .Subscribe(state => DataContext = new GitHubLinkChipViewModel(Link, state));
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) {
        _watch?.Dispose();
        _watch = null;
        _pressed = false;
        base.OnDetachedFromVisualTree(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e) {
        base.OnPointerPressed(e);
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pressed = true;
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e) {
        base.OnPointerReleased(e);
        if (!_pressed) return;
        _pressed = false;
        e.Handled = true;
        if (e.InitialPressMouseButton != MouseButton.Left) return;
        var open = this.FindAncestorOfType<MarkdownView>()?.OpenLink;
        if (open?.CanExecute(Url) == true) open.Execute(Url);
    }
}
