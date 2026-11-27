using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Views;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI.Reactive;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.Views.MarkdownViewHarness;

namespace Capacitor.App.Tests.Unit.Views;

/// A GitHub pull request or issue address in a chat bubble, the user's or the agent's, reads as a
/// chip naming `owner/repo#n` that opens the address as a link does, shows it on hover, takes a
/// keyboard stop, and announces the state once it arrives.
[NotInParallel("AvaloniaSession")]
public class GitHubLinkChipTests {
    const string Pull = "https://github.com/kurrent-io/kcap/pull/2625";

    static void Settle(Window window) {
        Dispatcher.UIThread.RunJobs();
        window.UpdateLayout();
    }

    [Test]
    [Arguments(MarkdownFlavor.Chat)]
    [Arguments(MarkdownFlavor.Prompt)]
    public Task A_bare_address_reads_as_a_chip_that_opens_it(MarkdownFlavor flavor) => RunOnUiAsync(async () => {
        var (window, view, opened) = Show($"Opened {Pull} for review.", flavor);
        try {
            var chip = All<GitHubLinkChip>(view).Single();
            await Assert.That(AutomationProperties.GetName(chip)).IsEqualTo("Pull request kurrent-io/kcap#2625");
            await Assert.That(Links(view)).IsEmpty();
            await Assert.That(Reads(Paragraphs(view).Single())).DoesNotContain("github.com");

            ClickCentre(window, chip);

            await Assert.That(opened).IsEquivalentTo(new[] { Pull });
        } finally { window.Close(); }
    });

    [Test]
    [Arguments("Opened [the pull request](" + Pull + ").", MarkdownFlavor.Chat)]
    [Arguments("Opened https://github.com/kurrent-io/kcap/pull/2625/files.", MarkdownFlavor.Chat)]
    [Arguments("Opened " + Pull + ".", MarkdownFlavor.GitHub)]
    public Task A_chosen_label_another_page_or_the_reader_keeps_the_link(string markdown, MarkdownFlavor flavor) => RunOnUiAsync(async () => {
        var (window, view, _) = Show(markdown, flavor);
        try {
            await Assert.That(All<GitHubLinkChip>(view)).IsEmpty();
            await Assert.That(Links(view).Count()).IsEqualTo(1);
        } finally { window.Close(); }
    });

    [Test]
    public Task Hovering_the_chip_shows_the_full_address() => RunOnUiAsync(async () => {
        var (window, view, _) = Show($"See {Pull}?w=1 now.");
        try {
            var chip = All<GitHubLinkChip>(view).Single();
            window.MouseMove(chip.TranslatePoint(new Point(chip.Bounds.Width / 2, chip.Bounds.Height / 2), window)!.Value);
            Dispatcher.UIThread.RunJobs();

            await Assert.That(ToolTip.GetTip(Viewer(view)) as string).IsEqualTo(Pull + "?w=1");
        } finally { window.Close(); }
    });

    [Test]
    public Task Tab_reaches_the_chip_in_reading_order_and_enter_opens_it() => RunOnUiAsync(async () => {
        var (window, view, opened) = Show($"First [a](https://example.com/a), then {Pull}, then [b](https://example.com/b).");
        try {
            var before = new Button { Content = "before" };
            window.Content = null;
            window.Content = new StackPanel { Children = { before, view } };
            Settle(window);
            before.Focus();
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            window.KeyPressQwerty(PhysicalKey.Tab, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(view.KeyboardLinkUrl).IsEqualTo(Pull);

            window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
            Dispatcher.UIThread.RunJobs();
            await Assert.That(opened).IsEquivalentTo(new[] { Pull });
        } finally { window.Close(); }
    });

    [Test]
    public Task The_chip_announces_the_state_once_it_arrives() => RunOnUiAsync(async () => {
        var answer = new TaskCompletionSource<GitHubLinkState?>();
        using var statuses = new GitHubLinkStatuses((_, _) => answer.Task, new FakeTimeProvider(), RxSchedulers.MainThreadScheduler);
        var (window, view, _) = Show($"Opened {Pull}.");
        try {
            MarkdownView.SetGitHubLinks(window, statuses);
            view.Text = $"Opened {Pull} for review.";
            Settle(window);
            var chip = All<GitHubLinkChip>(view).Single();
            await Assert.That(AutomationProperties.GetName(chip)).IsEqualTo("Pull request kurrent-io/kcap#2625");

            answer.SetResult(GitHubLinkState.Merged);
            for (var i = 0; i < 200 && statuses.Current(chip.Link) is null; i++) await Task.Delay(10);
            Settle(window);

            await Assert.That(AutomationProperties.GetName(chip)).IsEqualTo("Pull request kurrent-io/kcap#2625, Merged");
        } finally { window.Close(); }
    });
}
