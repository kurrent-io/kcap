using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.Tests.Unit.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Commands;
using Capacitor.Cli.Core.Http;

namespace Capacitor.App.Tests.Unit.Views;

/// The dialogs' action rows: dismiss first and primary last, Esc on the dismiss, Enter on the
/// primary unless pressing it removes, stops or approves something.
[NotInParallel("AvaloniaSession")]
public class DialogActionRowTests {
    sealed class NoApi : IFeedbackApi {
        public Task<FeedbackResult> SubmitAsync(FeedbackSubmission submission, CancellationToken ct = default) =>
            Task.FromResult<FeedbackResult>(new FeedbackResult.Sent("a@b.c"));
    }

    static List<Button> ActionRow(Control root, string lastButton) =>
        root.GetLogicalDescendants().OfType<StackPanel>()
            .Where(panel => panel.Orientation == Orientation.Horizontal)
            .Select(panel => panel.Children.OfType<Button>().ToList())
            .Single(buttons => buttons.Any(b => b.Name == lastButton));

    /// A Topmost prompt can appear while the user types elsewhere, so no key approves a launch.
    [Test]
    public async Task The_launch_prompt_ends_with_allow_once_and_no_key_approves() {
        var row = await AvaloniaSession.DispatchAsync(() => ActionRow(new ConsentPromptWindow(), "AllowOnceButton")
            .Select(b => (b.Name, b.IsCancel, b.IsDefault)).ToList());

        await Assert.That(string.Join(",", row.Select(b => b.Name))).IsEqualTo("DenyButton,AllowRememberButton,AllowOnceButton");
        await Assert.That(row[0].IsCancel).IsTrue();
        await Assert.That(row.Any(b => b.IsDefault)).IsFalse();
    }

    [Test]
    public Task Feedback_dismisses_first_sends_last_and_binds_both_keys() => AvaloniaSession.RunOnUiAsync(async () => {
        using var trailer = new BehaviorSubject<string>("trailer");
        using var vm = new FeedbackViewModel(new NoApi(), FeedbackCategory.Feedback, trailer, "macOS 15.6", null);
        var window = new FeedbackWindow { DataContext = vm };
        Dispatcher.UIThread.RunJobs();
        var row = ActionRow(window, "SendButton");

        await Assert.That(row[0].Name).IsEqualTo("CancelButton");
        await Assert.That(row[^1].Name).IsEqualTo("SendButton");
        await Assert.That(row[0].IsCancel).IsTrue();
        await Assert.That(row[^1].IsDefault).IsTrue();
    });

    /// Once sent, Close finishes the flow: it sits right of Send another and Enter presses it.
    [Test]
    public Task A_sent_feedback_ends_with_close_on_enter() => AvaloniaSession.RunOnUiAsync(async () => {
        using var trailer = new BehaviorSubject<string>("trailer");
        using var vm = new FeedbackViewModel(new NoApi(), FeedbackCategory.Feedback, trailer, "macOS 15.6", null);
        var window = new FeedbackWindow { DataContext = vm };
        try {
            window.Show();
            window.FindControl<TextBox>("MessageInput")!.Text = "Nice.";
            Dispatcher.UIThread.RunJobs();
            window.FindControl<Button>("SendButton")!.Command!.Execute(null);
            await WorkspaceFixtures.WaitUntilAsync(() => vm.IsSent, what: "feedback sent");
            Dispatcher.UIThread.RunJobs();
            var shown = ActionRow(window, "CloseButton").Where(b => b.IsVisible).ToList();

            await Assert.That(string.Join(",", shown.Select(b => b.Name))).IsEqualTo("SendAnotherButton,CloseButton");
            await Assert.That(shown[^1].IsDefault).IsTrue();
        } finally { window.Close(); }
    });

    /// A failed uninstall ends with Retry, and Enter does not run it: a retry removes files.
    [Test]
    public Task A_failed_uninstall_ends_with_retry_off_enter() => AvaloniaSession.RunOnUiAsync(async () => {
        var cli = new FakeKcapCli { UninstallBehavior = _ => Task.FromResult(
            new StreamingResult(1, false, [new StreamedLine(ProcessStreamKind.Stderr, "Could not stop the daemon.")])) };
        var run = new UninstallRunViewModel(cli, new KeptRemoval(), false, () => { }, action => action());
        var window = new UninstallWindow { DataContext = run };
        try {
            window.Show();
            await run.RunAsync();
            Dispatcher.UIThread.RunJobs();
            var shown = ActionRow(window, "RetryButton").Where(b => b.IsVisible).ToList();

            await Assert.That(string.Join(",", shown.Select(b => b.Name))).IsEqualTo("CloseButton,RetryButton");
            await Assert.That(shown[^1].IsDefault).IsFalse().Because("a retry removes files, so Enter does not run it");
        } finally { window.Close(); }
    });

    sealed class KeptRemoval : IAppRemoval {
        public Task<IReadOnlyList<string>> RemoveLinksAsync(CancellationToken ct) => Task.FromResult<IReadOnlyList<string>>([]);
        public bool DeleteSupportFiles() => true;
        public Task<bool> TrashBundleAsync(CancellationToken ct) => Task.FromResult(true);
        public void RevealBundle() { }
    }

    /// Removing a profile cannot be undone, so its Accept is off Enter; a reload keeps Enter.
    [Test]
    [Arguments(LifecyclePrompt.KindRemoveProfile, true)]
    [Arguments(LifecyclePrompt.KindReloadService, false)]
    public Task A_lifecycle_prompts_accept_is_off_enter_only_for_a_removal(string kind, bool destructive) => AvaloniaSession.RunOnUiAsync(async () => {
        var vm = new LifecyclePromptViewModel(new LifecyclePrompt(kind, null, null, false, "disclosure"), new TaskCompletionSource<bool>());
        var window = new LifecyclePromptWindow { DataContext = vm };
        Dispatcher.UIThread.RunJobs();
        var accept = window.FindControl<Button>("AcceptButton")!;

        await Assert.That(accept.IsDefault).IsEqualTo(!destructive);
    });

    [Test]
    public async Task Stopping_a_review_participant_is_off_enter() {
        var (themed, buttons) = await AvaloniaSession.DispatchAsync(() => {
            var window = App.BuildConfirmForceStopWindow("reviewer", new TaskCompletionSource<bool>());
            return (window.Background is not null, window.GetLogicalDescendants().OfType<Button>()
                .Select(b => (Content: b.Content as string, b.IsCancel, b.IsDefault)).ToList());
        });

        await Assert.That(themed).IsTrue();
        await Assert.That(string.Join(",", buttons.Select(b => b.Content))).IsEqualTo("Cancel,Stop anyway");
        await Assert.That(buttons[0].IsCancel).IsTrue();
        await Assert.That(buttons.Any(b => b.IsDefault)).IsFalse();
    }
}
