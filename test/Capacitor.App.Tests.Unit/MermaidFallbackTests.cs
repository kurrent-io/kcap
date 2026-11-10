using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using Capacitor.App.Views;
using ReactiveUI.Reactive;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class MermaidFallbackTests {
    static string Fence(string source, string language = "mermaid") => $"```{language}\n{source}\n```";
    static Button Action(Visual root, string kind) => All<Button>(root).Single(b => b.Classes.Contains(kind));
    static void Flush(Window window) { Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Dispatcher.UIThread.RunJobs(); }
    static void Press(Window window, PhysicalKey key, RawInputModifiers modifiers = RawInputModifiers.None) {
        window.KeyPressQwerty(key, modifiers);
        window.KeyReleaseQwerty(key, modifiers);
        Flush(window);
    }

    [Test]
    [Arguments("graph TD\n A[DIAGRAM_SENTINEL] --> B\n")]
    [Arguments("this is not valid Mermaid <img src='https://untrusted.example/pixel'>")]
    [Arguments("")]
    public Task A_GitHub_diagram_explains_the_fallback_and_copies_the_full_source(string source) => RunOnUiAsync(async () => {
        var (window, view, opened) = Show(Fence(source), MarkdownFlavor.GitHub);
        try {
            await Assert.That(All<MermaidFallback>(view)).Count().IsEqualTo(1);
            await Assert.That(All<TextBlock>(view).Select(Reads)).Contains("Mermaid diagram");
            await Assert.That(All<TextBlock>(view).Select(Reads)).Contains("Diagram preview is not available here. You can show or copy the source.");
            await Assert.That(Action(view, "markdown-mermaid-open").IsVisible).IsFalse();
            await Assert.That(Images(view)).IsEmpty();
            var copy = Action(view, "markdown-mermaid-copy");
            await Assert.That(ControlAutomationPeer.CreatePeerForElement(copy).GetName()).IsEqualTo("Copy source");
            ClickCentre(window, copy);
            await Assert.That(await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync()).IsEqualTo(source);
            ClickCentre(window, Action(view, "markdown-mermaid-toggle"));
            await Assert.That(All<Border>(view).Count(b => b.Classes.Contains("markdown-mermaid-source"))).IsEqualTo(1);
            await Assert.That(Images(view)).IsEmpty();
            await Assert.That(opened).IsEmpty();
        } finally { window.Close(); }
    });

    [Test]
    [Arguments(MarkdownFlavor.Chat)]
    [Arguments(MarkdownFlavor.Prompt)]
    public Task Other_flavors_keep_their_code_block_and_late_run_action(MarkdownFlavor flavor) => RunOnUiAsync(async () => {
        var (window, view, _) = Show(Fence("! echo unchanged"), flavor);
        var ran = new List<string>();
        using var run = ReactiveCommand.Create<string>(ran.Add);
        try {
            view.RunCode = run;
            Flush(window);
            await Assert.That(All<MermaidFallback>(view)).IsEmpty();
            var button = Action(view, "markdown-code-run");
            button.Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(ran).IsEquivalentTo(new[] { "! echo unchanged" });
        } finally { window.Close(); }
    });

    [Test]
    [Arguments("mermaid-ish")]
    [Arguments("csharp")]
    public Task Other_GitHub_fences_keep_the_existing_code_actions(string language) => RunOnUiAsync(async () => {
        var (window, view, _) = Show(Fence("literal source", language), MarkdownFlavor.GitHub);
        try {
            await Assert.That(All<MermaidFallback>(view)).IsEmpty();
            var copy = Action(view, "markdown-code-copy");
            copy.Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync()).IsEqualTo("literal source");
        } finally { window.Close(); }
    });

    [Test]
    public Task Diagram_source_never_becomes_runnable_when_a_command_arrives_or_changes() => RunOnUiAsync(async () => {
        var (window, view, _) = Show(Fence("! diagram source") + "\n\n" + Fence("! actual command", "bash"), MarkdownFlavor.GitHub);
        var ran = new List<string>();
        using var run = ReactiveCommand.Create<string>(ran.Add);
        try {
            ClickCentre(window, Action(view, "markdown-mermaid-toggle"));
            view.RunCode = run;
            Flush(window);
            await Assert.That(All<Button>(All<MermaidFallback>(view).Single()).Any(b => b.Classes.Contains("markdown-code-run"))).IsFalse();
            var button = Action(view, "markdown-code-run");
            button.Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(ran).IsEquivalentTo(new[] { "! actual command" });
            view.RunCode = null;
            view.RunCode = run;
            Flush(window);
            await Assert.That(All<Button>(view).Count(b => b.Classes.Contains("markdown-code-run"))).IsEqualTo(1);
        } finally { window.Close(); }
    });

    [Test]
    public Task Late_source_commands_keep_their_parameter_keyboard_behavior_and_detach_lifetime() => RunOnUiAsync(async () => {
        var (window, view, _) = Show(Fence("graph TD\n A-->B"), MarkdownFlavor.GitHub);
        using var allowed = new BehaviorSubject<bool>(true);
        var calls = new List<object?>();
        using var command = ReactiveCommand.Create<object?>(calls.Add, allowed);
        var first = new object();
        var second = new object();
        try {
            var open = Action(view, "markdown-mermaid-open");
            await Assert.That(open.IsVisible).IsFalse();
            view.OpenSourceCommandParameter = first;
            view.OpenSourceCommand = command;
            Flush(window);
            await Assert.That(open.IsVisible).IsTrue();
            await Assert.That(ControlAutomationPeer.CreatePeerForElement(open).GetName()).IsEqualTo("Open on GitHub");
            view.CanOpenSource = false;
            Flush(window);
            await Assert.That(open.IsVisible).IsFalse();
            view.CanOpenSource = true;
            Flush(window);
            await Assert.That(open.IsVisible).IsTrue();
            open.Focus();
            Press(window, PhysicalKey.Enter);
            ClickCentre(window, open);
            open.Focus();
            Press(window, PhysicalKey.Space);
            await Assert.That(calls).Count().IsEqualTo(3);
            await Assert.That(calls.All(value => ReferenceEquals(value, first))).IsTrue();
            allowed.OnNext(false);
            Flush(window);
            await Assert.That(open.IsEffectivelyEnabled).IsFalse();
            Press(window, PhysicalKey.Enter);
            await Assert.That(calls).Count().IsEqualTo(3);
            view.OpenSourceCommandParameter = second;
            allowed.OnNext(true);
            Flush(window);
            open.Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(ReferenceEquals(calls.Last(), second)).IsTrue();
            window.Content = null;
            Flush(window);
            await Assert.That(open.Command).IsNull();
            window.Content = view;
            Flush(window);
            open.Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(calls).Count().IsEqualTo(5);
            view.OpenSourceCommand = null;
            Flush(window);
            await Assert.That(open.IsVisible).IsFalse();
            view.OpenSourceCommand = command;
            view.Text = Fence("graph LR\n C-->D");
            Flush(window);
            await Assert.That(open.Command).IsNull();
            Action(view, "markdown-mermaid-open").Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(calls).Count().IsEqualTo(6);
        } finally { window.Close(); }
    });

    [Test]
    public Task Switching_flavor_replaces_nested_Mermaid_fallbacks_without_changing_other_fences() => RunOnUiAsync(async () => {
        var (window, view, _) = Show("> ```MeRmAiD\n> graph TD\n>  A-->B\n> ```\n\n" + Fence("graph LR\n C-->D"), MarkdownFlavor.GitHub);
        try {
            await Assert.That(All<MermaidFallback>(view)).Count().IsEqualTo(2);
            view.Flavor = MarkdownFlavor.Chat;
            Flush(window);
            await Assert.That(All<MermaidFallback>(view)).IsEmpty();
            await Assert.That(All<Button>(view).Count(b => b.Classes.Contains("markdown-code-copy"))).IsEqualTo(2);
            view.Flavor = MarkdownFlavor.GitHub;
            Flush(window);
            await Assert.That(All<MermaidFallback>(view)).Count().IsEqualTo(2);
        } finally { window.Close(); }
    });

    [Test]
    public Task The_disclosure_indexes_only_visible_source_for_selection_find_and_pointer_selection() => RunOnUiAsync(async () => {
        const string source = "graph TD\n A[DIAGRAM_SENTINEL] --> B";
        var (window, view, _) = Show("Before the diagram.\n\n" + Fence(source) + "\n\nAfter the diagram.", MarkdownFlavor.GitHub);
        try {
            view.SelectAll();
            await Assert.That(Viewer(view).GetSelectedText()).IsEqualTo("Before the diagram.\nAfter the diagram.");
            view.FindQuery = "DIAGRAM_SENTINEL";
            Flush(window);
            await Assert.That(view.FindMatchCount).IsEqualTo(0);
            var toggle = Action(view, "markdown-mermaid-toggle");
            toggle.Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(Action(view, "markdown-mermaid-toggle").IsFocused).IsTrue();
            await Assert.That(Action(view, "markdown-mermaid-toggle").Content).IsEqualTo("Hide source");
            await Assert.That(view.FindMatchCount).IsEqualTo(1);
            view.SelectAll();
            var selected = Viewer(view).GetSelectedText();
            await Assert.That(selected).IsEqualTo("Before the diagram.\n" + source + "\nAfter the diagram.");
            await Assert.That(selected.Split("DIAGRAM_SENTINEL", StringSplitOptions.None).Length).IsEqualTo(2);

            // The ordinary code block participates in real pointer hit testing, not just SelectAll.
            Viewer(view).ClearSelection();
            var code = (TextBlock)All<Border>(view).Single(b => b.Classes.Contains("markdown-mermaid-source")).Child!;
            var start = code.TextLayout.HitTestTextPosition(source.IndexOf("DIAGRAM_SENTINEL", StringComparison.Ordinal));
            var end = code.TextLayout.HitTestTextPosition(source.IndexOf("DIAGRAM_SENTINEL", StringComparison.Ordinal) + "DIAGRAM_SENTINEL".Length);
            var from = code.TranslatePoint(new Point(start.X + code.Padding.Left, start.Y + code.Padding.Top + start.Height / 2), window)!.Value;
            var to = code.TranslatePoint(new Point(end.X + code.Padding.Left, end.Y + code.Padding.Top + end.Height / 2), window)!.Value;
            window.MouseDown(from, MouseButton.Left);
            window.MouseMove(to, RawInputModifiers.LeftMouseButton);
            window.MouseUp(to, MouseButton.Left);
            Flush(window);
            await Assert.That(Viewer(view).GetSelectedText()).IsEqualTo("DIAGRAM_SENTINEL");

            Action(view, "markdown-mermaid-toggle").Focus();
            Press(window, PhysicalKey.Space);
            await Assert.That(Action(view, "markdown-mermaid-toggle").Content).IsEqualTo("Show source");
            await Assert.That(Action(view, "markdown-mermaid-toggle").IsFocused).IsTrue();
            await Assert.That(view.FindMatchCount).IsEqualTo(0);
            view.SelectAll();
            await Assert.That(Viewer(view).GetSelectedText()).IsEqualTo("Before the diagram.\nAfter the diagram.");
            Action(view, "markdown-mermaid-copy").Focus();
            Press(window, PhysicalKey.Enter);
            await Assert.That(await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync()).IsEqualTo(source);
        } finally { window.Close(); }
    });

    [Test]
    public Task Source_disclosures_are_independent_and_reset_for_a_new_document_or_flavor() => RunOnUiAsync(async () => {
        var markdown = Fence("graph TD\n A-->B") + "\n\n" + Fence("graph LR\n C-->D");
        var (window, view, _) = Show(markdown, MarkdownFlavor.GitHub);
        try {
            ClickCentre(window, All<Button>(view).First(b => b.Classes.Contains("markdown-mermaid-toggle")));
            await Assert.That(All<Border>(view).Count(b => b.Classes.Contains("markdown-mermaid-source"))).IsEqualTo(1);
            view.ReferenceBase = "https://github.com/example/repo";
            Flush(window);
            await Assert.That(All<Border>(view).Count(b => b.Classes.Contains("markdown-mermaid-source"))).IsEqualTo(1);
            view.Text = Fence("different document") + "\n\n" + Fence("different diagram");
            Flush(window);
            await Assert.That(All<Border>(view).Where(b => b.Classes.Contains("markdown-mermaid-source"))).IsEmpty();
            ClickCentre(window, All<Button>(view).First(b => b.Classes.Contains("markdown-mermaid-toggle")));
            view.Flavor = MarkdownFlavor.Chat;
            view.Flavor = MarkdownFlavor.GitHub;
            Flush(window);
            await Assert.That(All<Border>(view).Where(b => b.Classes.Contains("markdown-mermaid-source"))).IsEmpty();
        } finally { window.Close(); }
    });

    [Test]
    public Task A_long_source_expands_into_normal_document_flow_and_clipped_actions_remain_keyboard_visible() => RunOnUiAsync(async () => {
        var text = string.Join('\n', Enumerable.Range(0, 120).Select(i => $" A{i}[node {i}] --> A{i + 1}"));
        var view = new MarkdownView { Flavor = MarkdownFlavor.GitHub, Text = Fence(text), Width = 280 };
        var opened = new List<object?>();
        using var openCommand = ReactiveCommand.Create<object?>(opened.Add);
        view.OpenSourceCommand = openCommand;
        view.OpenSourceCommandParameter = "controlled source";
        var after = new Button { Content = "After the diagram" };
        // The production PR reader disables general focus scrolling to keep reader updates stable.
        var outer = new ScrollViewer { BringIntoViewOnFocusChange = false,
            Content = new StackPanel { Children = { view, after, new Border { Height = 700 } } } };
        var window = new Window { Content = outer, Width = 330, Height = 400 };
        window.Show();
        Flush(window);
        try {
            await Assert.That(All<MermaidFallback>(view).Single().Bounds.Height).IsLessThan(outer.Viewport.Height);
            await Assert.That(All<Border>(view).Where(b => b.Classes.Contains("markdown-mermaid-source"))).IsEmpty();
            var toggle = Action(view, "markdown-mermaid-toggle");
            toggle.Focus();
            var viewport = All<ScrollContentPresenter>(outer).First();
            var anchor = toggle.TranslatePoint(default, viewport)!.Value.Y;
            Press(window, PhysicalKey.Enter);
            toggle = Action(view, "markdown-mermaid-toggle");
            await Assert.That(toggle.IsFocused).IsTrue();
            await Assert.That(Math.Abs(toggle.TranslatePoint(default, viewport)!.Value.Y - anchor)).IsLessThan(1);
            var code = All<Border>(view).Single(b => b.Classes.Contains("markdown-mermaid-source"));
            await Assert.That(code.Bounds.Height).IsGreaterThan(outer.Viewport.Height);
            view.SelectAll();
            await Assert.That(Viewer(view).GetSelectedText()).IsEqualTo(text);
            Press(window, PhysicalKey.PageDown);
            await Assert.That(outer.Offset.Y).IsGreaterThan(0);
            Press(window, PhysicalKey.PageUp);
            await Assert.That(outer.Offset.Y).IsEqualTo(0);
            Press(window, PhysicalKey.PageDown);
            await Assert.That(outer.Offset.Y).IsGreaterThan(0);
            var copy = Action(view, "markdown-mermaid-copy");
            await Assert.That(copy.TranslatePoint(new Point(0, copy.Bounds.Height), viewport)!.Value.Y).IsLessThan(0);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            await Assert.That(copy.IsFocused).IsTrue();
            var copyOrigin = copy.TranslatePoint(default, viewport)!.Value;
            await Assert.That(new Rect(viewport.Bounds.Size).Contains(new Rect(copyOrigin, copy.Bounds.Size))).IsTrue();
            Press(window, PhysicalKey.Enter);
            await Assert.That(await TopLevel.GetTopLevel(view)!.Clipboard!.TryGetTextAsync()).IsEqualTo(text);
            Press(window, PhysicalKey.Tab, RawInputModifiers.Shift);
            var open = Action(view, "markdown-mermaid-open");
            await Assert.That(open.IsFocused).IsTrue();
            await Assert.That(new Rect(viewport.Bounds.Size).Contains(new Rect(open.TranslatePoint(default, viewport)!.Value, open.Bounds.Size))).IsTrue();
            Press(window, PhysicalKey.Enter);
            await Assert.That(opened).IsEquivalentTo(new object?[] { "controlled source" });
            // Command execution can disable the action briefly. Check traversal independently
            // from its transient CanExecute focus changes, starting at a known enabled action.
            await Assert.That(open.IsEffectivelyEnabled).IsTrue();
            open.Focus(NavigationMethod.Tab);
            Flush(window);
            await Assert.That(open.IsFocused).IsTrue();
            Press(window, PhysicalKey.Tab);
            await Assert.That(copy.IsFocused).IsTrue()
                .Because($"Tab moves from Open to Copy; actual focus: {window.FocusManager?.GetFocusedElement()}");
            Press(window, PhysicalKey.Tab);
            await Assert.That(toggle.IsFocused).IsTrue()
                .Because($"Tab moves from Copy to the source disclosure; actual focus: {window.FocusManager?.GetFocusedElement()}");
            Press(window, PhysicalKey.Tab);
            await Assert.That(after.IsFocused).IsTrue()
                .Because($"Tab leaves the disclosure actions; actual focus: {window.FocusManager?.GetFocusedElement()}");
        } finally { window.Close(); }
    });
}
