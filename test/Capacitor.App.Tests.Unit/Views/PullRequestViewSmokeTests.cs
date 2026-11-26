using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.Views;

[NotInParallel("AvaloniaSession")]
public class PullRequestViewSmokeTests {
    [Test]
    [Arguments("merged", "success", "Merged", "Checks passing")]
    [Arguments("open", "failure", "Open", "Checks failing")]
    public Task Structured_PR_card_tip_leaves_check_results_in_the_card(
            string lifecycle, string rollup, string stateText, string checksText) => RunOnUiAsync(async () => {
        using var appearance = new AppearanceScope();
        appearance.Apply(DesktopTheme.Dark);
        await using var host = new PullRequestViewTestHost();
        foreach (var link in host.Source.Links) host.Source.Lifecycles[link.Number] = lifecycle;
        host.Source.RollupFor = _ => rollup;
        await host.ShowAsync();
        var checks = host.Card.FindControl<Button>("SidebarChecksButton")!;
        await Assert.That(checks.GetVisualDescendants().OfType<TextBlock>()
            .Any(text => text.IsEffectivelyVisible && text.Text == checksText)).IsTrue();
        var number = host.Card.FindControl<TextBlock>("LifecycleNumber")!;
        var repository = host.Card.GetVisualDescendants().OfType<TextBlock>().Single(text =>
            text.IsEffectivelyVisible && text.Text == host.Model.RepositoryLabel);
        foreach (var label in new[] { number, repository }) {
            ToolTip.SetIsOpen(label, true);
            await Assert.That(ToolTip.GetIsOpen(label)).IsFalse();
        }
        var title = host.Card.FindControl<Button>("PullRequestTitleButton")!;
        title.Focus(NavigationMethod.Directional);
        AppearanceScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(AppearanceScope.Text(title)).IsEquivalentTo(
            new[] { "Private PR", stateText, "Pull request", $"{host.Model.RepositoryLabel} {host.Model.NumberLabel}" },
            CollectionOrdering.Matching);
        await Assert.That(AutomationProperties.GetHelpText(title)!.Contains(checksText, StringComparison.Ordinal)).IsFalse();
        await Assert.That(AutomationProperties.GetName(title)).IsEqualTo("Open pull request overview");
        var select = host.Card.FindControl<ComboBox>("PullRequestSelector")!;
        await Assert.That(select.IsEffectivelyVisible).IsTrue();
        select.Focus(NavigationMethod.Directional);
        await Assert.That(ToolTip.GetIsOpen(title)).IsFalse();
        await Assert.That(select.ItemCount).IsEqualTo(2);
    });

    [Test]
    public Task Structured_PR_picker_tips_follow_keyboard_focus_and_selection() => RunOnUiAsync(async () => {
        using var appearance = new AppearanceScope();
        appearance.Apply(DesktopTheme.Light);
        await using var host = new PullRequestViewTestHost();
        host.Source.Links = [
            FakePullRequestSource.Link(1) with { Title = "First linked PR", Lifecycle = "open" },
            FakePullRequestSource.Link(2) with { Title = "Second linked PR", Lifecycle = "merged" },
        ];
        await host.ShowAsync();
        var select = host.Card.FindControl<ComboBox>("PullRequestSelector")!;
        select.Focus(NavigationMethod.Tab);
        AppearanceScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(AppearanceScope.Text(select)).IsEquivalentTo(
            new[] { "First linked PR", "Open", "Pull request", "example/repo #1" },
            CollectionOrdering.Matching);
        await Assert.That(AutomationProperties.GetHelpText(select)).Contains("First linked PR").And.Contains("Pull request · example/repo #1");
        AppearanceScope.Press(host.Window, PhysicalKey.Escape);
        await Assert.That(ToolTip.GetIsOpen(select)).IsFalse();
        await Assert.That(select.IsFocused).IsTrue();

        AppearanceScope.Press(host.Window, PhysicalKey.Enter);
        await Assert.That(select.IsDropDownOpen).IsTrue();
        AppearanceScope.Press(host.Window, PhysicalKey.ArrowDown);
        var second = (ComboBoxItem)select.ContainerFromIndex(1)!;
        await Assert.That(second.IsFocused).IsTrue();
        await Assert.That(AutomationProperties.GetHelpText(second)).Contains("Second linked PR");
        AppearanceScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(AppearanceScope.Text(second)).IsEquivalentTo(
            new[] { "Second linked PR", "Merged", "Pull request", "example/repo #2" },
            CollectionOrdering.Matching);
        AppearanceScope.Press(host.Window, PhysicalKey.Escape);
        await Assert.That(ToolTip.GetIsOpen(second)).IsFalse();
        await Assert.That(select.IsDropDownOpen).IsTrue();
        await Assert.That(second.IsFocused).IsTrue();
        AppearanceScope.Press(host.Window, PhysicalKey.Enter);
        await Assert.That(select.IsDropDownOpen).IsFalse();
        await host.SettleAsync();
        await Assert.That(select.IsFocused).IsTrue();
        await Assert.That(select.SelectedIndex).IsEqualTo(1);

        AppearanceScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(AppearanceScope.Text(select)).Contains("Second linked PR");
        appearance.Apply(DesktopTheme.Dark);
        AppearanceScope.Pump(host.Window);
        await Assert.That(select.IsFocused).IsTrue();
        AppearanceScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(AppearanceScope.Text(select)).Contains("Second linked PR");
        await Assert.That(AutomationProperties.GetHelpText(select)).Contains("Second linked PR");
        AppearanceScope.Press(host.Window, PhysicalKey.Escape);
        AppearanceScope.Press(host.Window, PhysicalKey.Enter);
        await Assert.That(select.IsDropDownOpen).IsTrue();
        var choice = (ComboBoxItem)select.ContainerFromIndex(1)!;
        await Assert.That(choice.IsFocused).IsTrue();
        AppearanceScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(AppearanceScope.Text(choice)).Contains("Merged");
    });

    [Test]
    [Arguments(true, false)]
    [Arguments(false, false)]
    [Arguments(true, true)]
    [Arguments(false, true)]
    public Task Reader_is_lazy_and_reusable_for_live_and_ended_sessions_with_or_without_a_terminal(bool terminal, bool ended) => RunOnUiAsync(async () => {
        var daemon = new FakeDaemonClientService();
        var attach = new FakeTerminalAttachClientFactory();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time);
        var vm = new WorkspaceViewModel("agent", daemon, NewActions(), attach.Factory, () => new FakeTerminalSurface(), time,
            new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: source);
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        try {
            daemon.Agents.AddOrUpdate(Agent("agent", "claude", terminal, sessionId: "session") with { Status = ended ? "Completed" : "Running" });
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => vm.PullRequests.CanReveal, what: "PR loaded");
            Dispatcher.UIThread.RunJobs();
            var host = view.FindControl<ContentControl>("PullRequestHost")!;
            var terminalHost = view.FindControl<Control>("TerminalHost");
            var chatHost = view.FindControl<ChatTabView>("ChatHost");
            var chat = vm.Chat;
            if (chat is not null) chat.ComposerText = "Unsent draft";
            await Assert.That(host.Content).IsNull();
            await vm.ShowPullRequestCommand.Execute();
            Dispatcher.UIThread.RunJobs(); window.UpdateLayout();
            var reader = host.Content;
            await Assert.That(reader).IsTypeOf<PullRequestReader>();
            await Assert.That(((Control)reader!).IsEffectivelyVisible).IsTrue();
            await Assert.That(vm.HasBannerNotice).IsFalse();
            await Assert.That(vm.PullRequests.Description).IsEqualTo("Private description");
            await vm.ShowChatCommand.Execute();
            await vm.ShowPullRequestCommand.Execute();
            Dispatcher.UIThread.RunJobs();
            await Assert.That(ReferenceEquals(reader, host.Content)).IsTrue();
            await Assert.That(ReferenceEquals(terminalHost, view.FindControl<Control>("TerminalHost"))).IsTrue();
            await Assert.That(ReferenceEquals(chatHost, view.FindControl<ChatTabView>("ChatHost"))).IsTrue();
            await Assert.That(ReferenceEquals(chat, vm.Chat)).IsTrue();
            if (chat is not null) await Assert.That(chat.ComposerText).IsEqualTo("Unsent draft");
            vm.PullRequests.SetForeground(false);
            await Assert.That(vm.PullRequests.Description).IsNull();
        } finally { window.Close(); await vm.TeardownAsync(); }
    });

    [Test]
    public Task The_tab_requires_a_linked_PR_while_the_sidebar_keeps_the_empty_state() => RunOnUiAsync(async () => {
        var daemon = new FakeDaemonClientService();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time) { Links = [] };
        var vm = new WorkspaceViewModel("agent", daemon, NewActions(), new FakeTerminalAttachClientFactory().Factory, () => new FakeTerminalSurface(), time,
            new RecordingOpener(), new FakePermissionService(), new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: source);
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1200, Height = 800 };
        window.Show();
        try {
            daemon.Agents.AddOrUpdate(Agent("agent", "claude", hasTerminal: false, sessionId: "session"));
            await (vm.Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            var pane = view.FindControl<WorkContextView>("WorkContextHost")!;
            var tab = view.FindControl<Button>("PullRequestTabButton")!;
            var section = pane.FindControl<StackPanel>("PullRequestSection")!;
            var card = pane.FindControl<PullRequestCard>("PullRequestCard")!;
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => source.Lists == 1 && !vm.PullRequests.IsReading, what: "empty PR list applied");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(tab.IsVisible).IsFalse();
            await Assert.That(section.IsVisible).IsTrue();
            await Assert.That(card.IsEffectivelyVisible).IsTrue();
            await Assert.That(vm.PullRequests.Title).IsEqualTo("");
            await Assert.That(vm.PullRequests.Notice).IsEqualTo("No pull requests yet.");
            await Assert.That(vm.WorkContext.ShowsPullRequestEmpty).IsFalse();

            source.Links = [FakePullRequestSource.Link(1)];
            time.Advance(TimeSpan.FromSeconds(16));
            await vm.PullRequests.RefreshCommand.Execute();
            await WaitUntilAsync(() => vm.PullRequests.CanReveal, what: "linked PR loaded");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(tab.IsVisible).IsTrue();
            await Assert.That(section.IsVisible).IsTrue();
            await Assert.That(card.IsEffectivelyVisible).IsTrue();

            await vm.ShowPullRequestCommand.Execute();
            source.Links = [];
            time.Advance(TimeSpan.FromSeconds(16));
            await vm.PullRequests.RefreshCommand.Execute();
            await WaitUntilAsync(() => !vm.PullRequests.HasPullRequest, what: "PR unlinked");
            Dispatcher.UIThread.RunJobs();
            await Assert.That(tab.IsVisible).IsFalse();
            await Assert.That(section.IsVisible).IsTrue();
            await Assert.That(card.IsEffectivelyVisible).IsTrue();
            await Assert.That(vm.PullRequests.Notice).IsEqualTo("No pull requests yet.");
            await Assert.That(vm.WorkContext.ShowsPullRequestEmpty).IsFalse();
            await Assert.That(pane.FindControl<TextBlock>("PullRequestNumberMeta")!.IsEffectivelyVisible).IsFalse();
            await Assert.That(vm.IsChatActive).IsTrue();
        } finally { window.Close(); await vm.TeardownAsync(); }
    });
}
