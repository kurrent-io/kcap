using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Input;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Views;

public partial class ChatTabViewSmokeTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Preview_composer_keeps_Send_at_the_bottom_as_a_draft_grows_and_rolls_back() =>
        RunOnUiAsync(async () => {
            var reading = new DesktopReadingResources(Application.Current!);
            var host = new Host(show: false);
            try {
                host.Window.Height = 390;
                host.Show();
                var send = host.View.FindControl<Button>("SendButton")!;
                var stockHeight = host.Composer.Bounds.Height;
                reading.Apply(new DesktopAppearancePreferences());
                var client = await host.AttachAsync(Tmp.CreateFile("stationary.jsonl", UserLine));
                host.Settle();
                var emptyHeight = host.Composer.Bounds.Height;
                var actionY = send.TranslatePoint(default, host.Window)!.Value.Y;
                await Assert.That(emptyHeight).IsLessThan(stockHeight);

                host.Type("first line");
                host.PressEnter(RawInputModifiers.Shift);
                host.Type("second line");
                host.Settle();
                await Assert.That(host.Composer.Bounds.Height).IsGreaterThan(emptyHeight);
                await Assert.That(send.TranslatePoint(default, host.Window)!.Value.Y).IsEqualTo(actionY);

                for (var i = 0; i < 12; i++) {
                    host.PressEnter(RawInputModifiers.Shift);
                    host.Type("more draft");
                }
                host.Settle();
                var editorScroll = host.Composer.GetVisualDescendants().OfType<ScrollViewer>().Single();
                await Assert.That(editorScroll.Extent.Height).IsGreaterThan(editorScroll.Viewport.Height);
                await Assert.That(editorScroll.Offset.Y).IsGreaterThan(0);
                await Assert.That(send.TranslatePoint(default, host.Window)!.Value.Y).IsEqualTo(actionY);
                await Assert.That(client.SentInput).IsEmpty();
                var draft = host.Chat.ComposerText;
                reading.Apply(null);
                host.Settle();
                await Assert.That(host.Chat.ComposerText).IsEqualTo(draft);
                await Assert.That(host.Composer.IsFocused).IsTrue();
            } finally {
                reading.Apply(null);
                await host.CloseAsync();
            }
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Preview_account_limits_open_by_keyboard_and_dismiss_to_the_account_button() =>
        RunWithNativePopupRootsAsync(async () => {
            var reading = new DesktopReadingResources(Application.Current!);
            var palette = new ClassicPreviewPalette(Application.Current!);
            using var readings = new BehaviorSubject<AccountsSnapshotDto?>(StripReadings.Snapshot(
                StripReadings.Account("acc1", StripReadings.Blocked(TimeSpan.FromMinutes(65),
                    StripReadings.Window(300, 100, TimeSpan.FromMinutes(65)),
                    StripReadings.Window(10080, 81, TimeSpan.FromDays(3)),
                    StripReadings.Window(1440, 45, TimeSpan.FromHours(12))))));
            var host = new Host(show: false, accountReadings: readings);
            try {
                palette.TryApply(ClassicAppearance.Light);
                reading.Apply(new DesktopAppearancePreferences());
                host.Window.Width = 460;
                host.Window.Height = 390;
                host.Show();
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = Tmp.CreateFile("limits.jsonl", UserLine),
                    AccountDirectory = StripReadings.Directory, AccountId = "acc1", AccountLabel = "work",
                });
                await WaitUntilAsync(() => { host.Settle(); return host.Chat.ShowsAccountUsage; }, what: "account limits");

                var account = host.View.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.Name == "AccountLimitsButton" && b.IsEffectivelyVisible);
                await Assert.That(AutomationProperties.GetName(account)).IsEqualTo("Account limits");
                await Assert.That(host.View.FindControl<Border>("AccountStrip")!.IsEffectivelyVisible).IsFalse();
                var flyout = (Flyout)account.Flyout!;
                var detailScroll = (ScrollViewer)flyout.Content!;
                NavigationMethod? focusMethod = null;
                detailScroll.GotFocus += (_, e) => focusMethod = e.NavigationMethod;
                account.Focus(NavigationMethod.Tab);
                host.Press(PhysicalKey.Enter);
                await Assert.That(flyout.IsOpen).IsTrue();
                await Assert.That(TopLevel.GetTopLevel(detailScroll)).IsSameReferenceAs(host.Window);
                var texts = ((Control)flyout.Content!).GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToArray();
                await Assert.That(texts).Contains("Limit hit · resets in 1h 05m");
                await Assert.That(texts).Contains("100%");
                await Assert.That(texts).Contains("81%");
                await Assert.That(texts).Contains("resets in 1h 05m");
                await Assert.That(detailScroll.IsFocused).IsTrue();
                await Assert.That(focusMethod).IsEqualTo(NavigationMethod.Tab);
                host.Press(PhysicalKey.PageDown);
                host.Settle();
                await Assert.That(detailScroll.Offset.Y).IsGreaterThan(0);
                host.Press(PhysicalKey.Escape);
                await Assert.That(flyout.IsOpen).IsFalse();
                await Assert.That(account.IsFocused).IsTrue();

                Click(host, account);
                await Assert.That(flyout.IsOpen).IsTrue();
                await Assert.That(detailScroll.IsFocused).IsTrue();
                await Assert.That(focusMethod).IsEqualTo(NavigationMethod.Pointer);
                host.Press(PhysicalKey.PageUp);
                host.Settle();
                await Assert.That(detailScroll.Offset.Y).IsEqualTo(0);
                host.Press(PhysicalKey.PageDown);
                host.Settle();
                await Assert.That(detailScroll.Offset.Y).IsGreaterThan(0);
                host.Press(PhysicalKey.Escape);
                await Assert.That(flyout.IsOpen).IsFalse();
                await Assert.That(account.IsFocused).IsTrue();

                reading.Apply(null);
                host.Settle();
                await Assert.That(account.IsEffectivelyVisible && account.IsAttachedToVisualTree()).IsFalse();
                await Assert.That(host.View.FindControl<Border>("AccountStrip")!.IsEffectivelyVisible).IsTrue();
            } finally {
                reading.Apply(null);
                palette.TryApply(ClassicAppearance.Original);
                await host.CloseAsync();
            }
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Preview_account_limits_explain_missing_and_unknown_usage_until_bars_arrive() =>
        RunWithNativePopupRootsAsync(async () => {
            var reading = new DesktopReadingResources(Application.Current!);
            using var readings = new BehaviorSubject<AccountsSnapshotDto?>(StripReadings.Snapshot());
            var host = new Host(show: false, accountReadings: readings);
            try {
                reading.Apply(new DesktopAppearancePreferences());
                host.Show();
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = Tmp.CreateFile("limits-summary.jsonl", UserLine),
                    AccountDirectory = StripReadings.Directory, AccountId = "acc1", AccountLabel = "work",
                });
                Button? account = null;
                await WaitUntilAsync(() => {
                    host.Settle();
                    account = host.View.GetVisualDescendants().OfType<Button>()
                        .SingleOrDefault(b => b is { Name: "AccountLimitsButton", IsEffectivelyVisible: true });
                    return account is not null;
                }, what: "the Limits button");
                Click(host, account!);
                var flyout = (Flyout)account!.Flyout!;
                var detail = (ScrollViewer)flyout.Content!;
                await Assert.That(flyout.IsOpen).IsTrue();
                await Assert.That(VisibleTexts(detail)).Contains("No usage reading available");

                readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", null)));
                host.Settle();
                await Assert.That(VisibleTexts(detail)).Contains("no reading yet");
                await Assert.That(VisibleTexts(detail)).DoesNotContain("No usage reading available");

                var window = StripReadings.Window(300, 42, TimeSpan.FromHours(2));
                readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(window with { LimitId = "sonnet" }))));
                host.Settle();
                await Assert.That(VisibleTexts(detail)).Contains("usage unknown · +1 per model");
                await Assert.That(VisibleTexts(detail)).Contains("updated just now");

                readings.OnNext(StripReadings.Snapshot(StripReadings.Account("acc1", StripReadings.Reading(window))));
                host.Settle();
                var texts = VisibleTexts(detail);
                await Assert.That(texts).Contains("42%");
                await Assert.That(texts).DoesNotContain("usage unknown · +1 per model");
                await Assert.That(texts).DoesNotContain("No usage reading available");
            } finally {
                reading.Apply(null);
                await host.CloseAsync();
            }
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Preview_inline_recovery_keeps_the_draft_and_attachment() => RunOnUiAsync(async () => {
        var reading = new DesktopReadingResources(Application.Current!);
        var host = new Host();
        try {
            reading.Apply(new DesktopAppearancePreferences());
            var retried = false;
            using var retryCommand = ReactiveUI.Reactive.ReactiveCommand.Create(() => { retried = true; });
            host.Chat.WorkspaceNotice = SessionConnectionPresentation.Failed(retryCommand);
            host.Type("keep this draft");
            host.Chat.Tray.AddAll([new StagedAttachment("notes.txt", "text/plain", new byte[] { 1 })]);
            host.PressEnter(RawInputModifiers.None);
            host.Settle();
            var feedback = host.View.GetVisualDescendants().OfType<TextBlock>()
                .Single(t => t.Name == "ComposerFeedbackText");
            await Assert.That(feedback.Text).IsEqualTo("Couldn’t reconnect to this session");
            var peer = ControlAutomationPeer.CreatePeerForElement(feedback);
            await Assert.That(peer.GetLiveSetting()).IsEqualTo(AutomationLiveSetting.Polite);
            await Assert.That(peer.GetName()).IsEqualTo("Couldn’t reconnect to this session");
            await Assert.That(host.Composer.IsFocused).IsTrue();
            var retry = ((Grid)feedback.Parent!).Children.OfType<Button>().Single();
            Click(host, retry);
            await Assert.That(retried).IsTrue();
            await Assert.That(host.Chat.ComposerText).IsEqualTo("keep this draft");
            await Assert.That(Chips(host).Single().FileName).IsEqualTo("notes.txt");
            var remove = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "ChipRemove");
            Click(host, remove);
            await Assert.That(host.Chat.Tray.HasAttachments).IsFalse();
            await Assert.That(host.Chat.ComposerText).IsEqualTo("keep this draft");

            var client = await host.AttachAsync(Tmp.CreateFile("recovered.jsonl", UserLine));
            host.Chat.WorkspaceNotice = null;
            host.Settle();
            await Assert.That(peer.GetLiveSetting()).IsEqualTo(AutomationLiveSetting.Off);
            await Assert.That(host.Chat.ComposerText).IsEqualTo("keep this draft");
            host.Composer.Focus();
            await host.SendAsync();
            await Assert.That(host.Chat.ComposerText).IsEmpty();
            await Assert.That(client.SentInput.Count).IsEqualTo(2);
            await Assert.That(client.SentInput[0]).IsEquivalentTo(TerminalInputEncoder.Paste("keep this draft"));
            await Assert.That(client.SentInput[1]).IsEquivalentTo(new byte[] { 13 });
        } finally {
            try { await host.CloseAsync(); }
            finally { reading.Apply(null); }
        }
    });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Preview_a_failed_upload_returns_to_the_same_draft_and_keeps_the_file() =>
        RunOnUiAsync(async () => {
            var reading = new DesktopReadingResources(Application.Current!);
            var host = new Host();
            try {
                reading.Apply(new DesktopAppearancePreferences());
                var client = await host.AttachAsync(Tmp.CreateFile("failed-upload.jsonl", UserLine));
                host.AllowAttachments();
                host.Type("send with notes");
                host.Chat.Tray.AddAll([new StagedAttachment("notes.txt", "text/plain", new byte[] { 1 })]);

                var send = host.View.FindControl<Button>("SendButton")!;
                Click(host, send);

                await Assert.That(host.View.GetVisualDescendants().OfType<TextBlock>()
                    .Single(t => t.Name == "ComposerFeedbackText").Text).Contains("sign in to attach files");
                await Assert.That(host.Composer.IsFocused).IsTrue();
                await Assert.That(host.Chat.ComposerText).IsEqualTo("send with notes");
                await Assert.That(Chips(host).Single().FileName).IsEqualTo("notes.txt");
                await Assert.That(client.SentInput).IsEmpty();
            } finally {
                reading.Apply(null);
                await host.CloseAsync();
            }
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Preview_terminal_choices_scroll_and_accept_keyboard_input_while_the_draft_is_blocked() =>
        RunOnUiAsync(async () => {
            var reading = new DesktopReadingResources(Application.Current!);
            var host = new Host(show: false);
            try {
                reading.Apply(new DesktopAppearancePreferences());
                host.Window.Height = 365;
                host.Show();
                var path = Tmp.CreateFile("limit-menu.jsonl", UserLine);
                var client = await host.AttachAsync(path);
                host.Type("hold my draft");
                host.Daemon.Agents.AddOrUpdate(Agent("a1", "claude", hasTerminal: true) with {
                    TranscriptPath = path,
                    UsageLimit = new(UsageLimitKinds.Blocked, "You hit your session limit", "What do you want to do?", [
                        new(1, "Stop and wait for the reset"), new(2, "Keep waiting"), new(3, "Ask your admin for more usage"),
                    ]),
                });
                host.Settle();

                await Assert.That(host.Composer.IsEnabled).IsFalse();
                var context = host.View.FindControl<ScrollViewer>("ComposerPromptContext")!;
                await Assert.That(context.Focus(NavigationMethod.Tab)).IsTrue();
                host.Press(PhysicalKey.PageDown);
                host.Settle();
                await Assert.That(context.Offset.Y).IsGreaterThan(0);

                var choice = context.GetVisualDescendants().OfType<Button>()
                    .Single(b => b.DataContext is TerminalMenuChoiceViewModel { Label: "Ask your admin for more usage" }
                        && b.Content is TextBlock);
                choice.Focus(NavigationMethod.Tab);
                host.Press(PhysicalKey.Enter);
                await WaitUntilAsync(() => client.SentInput.Any(bytes => bytes is [(byte)'3']), what: "terminal choice written");
                await Assert.That(host.Chat.ComposerText).IsEqualTo("hold my draft");
            } finally {
                reading.Apply(null);
                await host.CloseAsync();
            }
        });

    [Test]
    [NotInParallel("AvaloniaSession")]
    public Task Composer_preedit_Enter_does_not_send_the_uncommitted_text() => RunOnUiAsync(async () => {
        var host = new Host();
        try {
            var client = await host.AttachAsync(Tmp.CreateFile("preedit.jsonl", UserLine));
            host.Type("draft");
            host.Settle();
            var presenter = host.Composer.GetVisualDescendants().OfType<TextPresenter>().Single();
            presenter.SetValue(TextPresenter.PreeditTextProperty, "候補");
            host.PressEnter(RawInputModifiers.None);
            await Assert.That(client.SentInput).IsEmpty();
            await Assert.That(host.Chat.ComposerText).IsEqualTo("draft");

            presenter.ClearValue(TextPresenter.PreeditTextProperty);
            host.PressEnter(RawInputModifiers.None);
            await WaitUntilAsync(() => client.SentInput.Count == 1, what: "the committed send");
        } finally { await host.CloseAsync(); }
    });
}
