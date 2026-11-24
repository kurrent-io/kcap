using Avalonia;
using Capacitor.Cli.Core.PullRequests;
using Avalonia.VisualTree;
using System.Reactive.Linq;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
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
    public Task Pointer_opened_PR_picker_commits_the_first_choice_with_Home_and_Return() => RunOnUiAsync(async () => {
        using var preview = new TooltipPreviewScope();
        preview.Enable(ClassicAppearance.Light, previewStatuses: true);
        await using var host = new PullRequestViewTestHost();
        host.Window.Width = 1400;
        host.Window.Height = 760;
        host.Source.Links = Enumerable.Range(1, 5).Select(FakePullRequestSource.Link).ToArray();
        host.Source.Lifecycles[5] = "closed";
        host.Source.TitleFor = subject => subject.Number == 1 ? "Correct the gateway retry policy" : "Other pull request";
        host.Source.RollupFor = subject => subject.Number == 1 ? "failure" : "success";
        await host.ShowAsync();

        var picker = host.Card.FindControl<ComboBox>("PullRequestSelector")!;
        picker.SelectedIndex = 4;
        await host.SettleAsync();
        await Assert.That(host.Model.LifecycleStatus.Text).IsEqualTo("Closed");

        var point = picker.TranslatePoint(new Point(picker.Bounds.Width / 2, picker.Bounds.Height / 2), host.Window)!.Value;
        host.Window.MouseDown(point, MouseButton.Left);
        host.Window.MouseUp(point, MouseButton.Left);
        TooltipPreviewScope.Pump(host.Window);
        await Assert.That(picker.IsDropDownOpen).IsTrue();

        TooltipPreviewScope.Press(host.Window, PhysicalKey.Home);
        await Assert.That(picker.ContainerFromIndex(0)!.IsFocused).IsTrue();
        await Assert.That(picker.SelectedIndex).IsEqualTo(4);
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Enter);
        await host.SettleAsync();

        await Assert.That(picker.IsDropDownOpen).IsFalse();
        await Assert.That(picker.IsFocused).IsTrue();
        var labels = host.Card.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
        foreach (var label in new[] { "Correct the gateway retry policy", "Open", "Checks failed" })
            await Assert.That(labels).Contains(label);
    });

    [Test]
    public Task Lifecycle_and_actionable_evidence_survive_keyboard_selection_theme_switch_and_rollback() => RunOnUiAsync(async () => {
        using var preview = new TooltipPreviewScope();
        preview.Enable(ClassicAppearance.Light, previewStatuses: true);
        await using var host = new PullRequestViewTestHost();
        host.Source.Links = Enumerable.Range(1, 4).Select(number => FakePullRequestSource.Link(number)).ToArray();
        host.Source.Lifecycles[2] = "draft";
        host.Source.Lifecycles[3] = "merged";
        host.Source.Lifecycles[4] = "closed";
        host.Source.OverviewResponses.Enqueue((subject, _) => {
            var read = host.Source.Overview(subject);
            return Task.FromResult(read with { Data = read.Data! with {
                Checks = new() { Availability = new() { Status = "ready" }, Rollup = "failure" },
                Mergeable = false, ReviewDecision = "changes_requested",
            } });
        });
        host.Model.SelectSubject(PullRequestWire.Subject(host.Source.Links[0]));
        await host.ShowAsync();
        var title = host.Card.FindControl<Button>("PullRequestTitleButton")!;
        title.Focus(NavigationMethod.Tab);
        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        var lines = TooltipPreviewScope.Text(title);
        await Assert.That(lines).Contains("Open");
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Escape);
        await Assert.That(title.IsFocused).IsTrue();
        var visibleCardText = host.Card.GetVisualDescendants().OfType<TextBlock>()
            .Where(text => text.IsEffectivelyVisible).Select(text => text.Text).ToArray();
        foreach (var text in new[] { "Checks failed", "Merge conflicts", "Changes requested" }) {
            await Assert.That(visibleCardText).Contains(text);
            await Assert.That(lines.Contains(text)).IsFalse();
        }

        var picker = host.Card.FindControl<ComboBox>("PullRequestSelector")!;
        picker.Focus(NavigationMethod.Tab);
        foreach (var lifecycle in new[] { "Draft", "Merged", "Closed" }) {
            TooltipPreviewScope.Press(host.Window, PhysicalKey.Enter);
            TooltipPreviewScope.Press(host.Window, PhysicalKey.ArrowDown);
            TooltipPreviewScope.Press(host.Window, PhysicalKey.Enter);
            await host.SettleAsync();
            await Assert.That(host.Reader.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == lifecycle)).IsTrue();
        }

        preview.Enable(ClassicAppearance.Dark, previewStatuses: true);
        TooltipPreviewScope.Pump(host.Window);
        await Assert.That(picker.IsFocused).IsTrue();
        await Assert.That(host.Model.LifecycleStatus.Text).IsEqualTo("Closed");
        preview.Disable();
        TooltipPreviewScope.Pump(host.Window);
        await Assert.That(picker.IsFocused).IsTrue();
        await Assert.That(host.Reader.GetVisualDescendants().OfType<TextBlock>().Any(t => t.IsEffectivelyVisible && t.Text == "Closed")).IsTrue();
    });

    [Test]
    [Arguments("merged", "success", "Merged", "Checks passing")]
    [Arguments("open", "failure", "Open", "Checks failing")]
    public Task Structured_PR_card_tip_leaves_check_results_in_the_card(
            string lifecycle, string rollup, string stateText, string checksText) => RunOnUiAsync(async () => {
        using var preview = new TooltipPreviewScope();
        preview.Enable(ClassicAppearance.Dark, previewStatuses: lifecycle == "merged");
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
        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(TooltipPreviewScope.Text(title)).IsEquivalentTo(
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
        using var preview = new TooltipPreviewScope();
        preview.Enable(ClassicAppearance.Light);
        await using var host = new PullRequestViewTestHost();
        host.Source.Links = [
            FakePullRequestSource.Link(1) with { Title = "First linked PR", Lifecycle = "open" },
            FakePullRequestSource.Link(2) with { Title = "Second linked PR", Lifecycle = "merged" },
        ];
        await host.ShowAsync();
        var select = host.Card.FindControl<ComboBox>("PullRequestSelector")!;
        select.Focus(NavigationMethod.Tab);
        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(TooltipPreviewScope.Text(select)).IsEquivalentTo(
            new[] { "First linked PR", "Open", "Pull request", "example/repo #1" },
            CollectionOrdering.Matching);
        await Assert.That(AutomationProperties.GetHelpText(select)).Contains("First linked PR").And.Contains("Pull request · example/repo #1");
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Escape);
        await Assert.That(ToolTip.GetIsOpen(select)).IsFalse();
        await Assert.That(select.IsFocused).IsTrue();

        TooltipPreviewScope.Press(host.Window, PhysicalKey.Enter);
        await Assert.That(select.IsDropDownOpen).IsTrue();
        TooltipPreviewScope.Press(host.Window, PhysicalKey.ArrowDown);
        var second = (ComboBoxItem)select.ContainerFromIndex(1)!;
        await Assert.That(second.IsFocused).IsTrue();
        await Assert.That(AutomationProperties.GetHelpText(second)).Contains("Second linked PR");
        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(TooltipPreviewScope.Text(second)).IsEquivalentTo(
            new[] { "Second linked PR", "Merged", "Pull request", "example/repo #2" },
            CollectionOrdering.Matching);
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Escape);
        await Assert.That(ToolTip.GetIsOpen(second)).IsFalse();
        await Assert.That(select.IsDropDownOpen).IsTrue();
        await Assert.That(second.IsFocused).IsTrue();
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Enter);
        await Assert.That(select.IsDropDownOpen).IsFalse();
        await host.SettleAsync();
        await Assert.That(select.IsFocused).IsTrue();
        await Assert.That(select.SelectedIndex).IsEqualTo(1);

        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(TooltipPreviewScope.Text(select)).Contains("Second linked PR");
        preview.Disable();
        TooltipPreviewScope.Pump(host.Window);
        await Assert.That(select.IsFocused).IsTrue();
        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(TooltipPreviewScope.Text(select)).Contains("Second linked PR");
        await Assert.That(AutomationProperties.GetHelpText(select)).Contains("Second linked PR");
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Escape);
        TooltipPreviewScope.Press(host.Window, PhysicalKey.Enter);
        await Assert.That(select.IsDropDownOpen).IsTrue();
        var choice = (ComboBoxItem)select.ContainerFromIndex(1)!;
        await Assert.That(choice.IsFocused).IsTrue();
        TooltipPreviewScope.Press(host.Window, PhysicalKey.F1, RawInputModifiers.Shift);
        await Assert.That(TooltipPreviewScope.Text(choice)).Contains("Merged");
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
    public Task Unified_reader_uses_the_workspace_card_shortcuts_and_restores_stock_focus_on_rollback() => RunOnUiAsync(async () => {
        using var preview = new TooltipPreviewScope();
        preview.Enable(ClassicAppearance.Light);
        var daemon = new FakeDaemonClientService();
        var time = new FakeTimeProvider();
        var source = new FakePullRequestSource(time) { TotalPages = 1, RollupFor = _ => "failure" };
        source.OverviewResponses.Enqueue((subject, _) => {
            var read = source.Overview(subject);
            return Task.FromResult(read with { Data = read.Data! with { ReviewDecision = "changes_requested", Mergeable = false } });
        });
        var vm = new WorkspaceViewModel("agent", daemon, NewActions(), new FakeTerminalAttachClientFactory().Factory,
            () => new FakeTerminalSurface(), time, new RecordingOpener(), new FakePermissionService(),
            new FakeWorkContextSource(), new ScriptedLocalControlOps(), new NoAttachmentUploader(), pullRequests: source);
        var view = new WorkspaceView { DataContext = vm };
        var window = new Window { Content = view, Width = 1400, Height = 760 };
        var host = view.FindControl<PullRequestReaderHost>("PullRequestHost")!;
        window.Resources[PullRequestReaderHost.UnifiedReaderResource] = true;
        window.Show();
        try {
            daemon.Agents.AddOrUpdate(Agent("agent", "claude", false, sessionId: "session"));
            vm.PullRequests!.SetForeground(true);
            await WaitUntilAsync(() => vm.PullRequests is { CanReveal: true, IsReading: false });
            TooltipPreviewScope.Pump(window);
            await Assert.That(host.Content).IsNull();
            await Assert.That(source.Pages).IsEqualTo(0);

            var card = view.GetVisualDescendants().OfType<PullRequestCard>().Single();
            var checks = card.FindControl<Button>("SidebarChecksButton")!;
            await Assert.That(checks.Focus(NavigationMethod.Tab)).IsTrue();
            TooltipPreviewScope.Press(window, PhysicalKey.Enter);
            await WaitUntilAsync(() => host.Content is UnifiedPullRequestReader &&
                source.PageReads.Any(request => request.Section == "checks") && !vm.PullRequests.IsReading);
            TooltipPreviewScope.Pump(window);
            var reader = (UnifiedPullRequestReader)host.Content!;
            var group = reader.FindControl<UnifiedPullRequestGroupView>("Checks")!;
            await Assert.That(group.Heading.IsFocused).IsTrue();
            await Assert.That(reader.GetVisualDescendants().OfType<TabStrip>().Any()).IsFalse();

            var reviews = card.FindControl<Button>("SidebarReviewsButton")!;
            await Assert.That(reviews.Focus(NavigationMethod.Tab)).IsTrue();
            TooltipPreviewScope.Press(window, PhysicalKey.Enter);
            var reviewers = reader.FindControl<UnifiedPullRequestGroupView>("Reviewers")!;
            await WaitUntilAsync(() => reviewers.Heading.IsFocused && !vm.PullRequests.IsReading);
            await Assert.That(source.PageReads.Any(request => request.Section == "reviewers")).IsTrue();

            var conflicts = card.FindControl<Button>("SidebarConflictsButton")!;
            await Assert.That(conflicts.Focus(NavigationMethod.Tab)).IsTrue();
            TooltipPreviewScope.Press(window, PhysicalKey.Enter);
            await Assert.That(reader.FindControl<TextBlock>("ConflictsHeading")!.IsFocused).IsTrue();

            checks.Focus(NavigationMethod.Tab);
            TooltipPreviewScope.Press(window, PhysicalKey.Enter);
            window.Resources.Remove(PullRequestReaderHost.UnifiedReaderResource);
            TooltipPreviewScope.Pump(window);
            var classic = (PullRequestReader)host.Content!;
            var tabs = classic.FindControl<TabStrip>("ReaderTabs")!;
            await Assert.That(tabs.SelectedIndex).IsEqualTo(1);
            await Assert.That(((Control)tabs.SelectedItem!).IsFocused).IsTrue();
            await Assert.That(card.FindControl<ComboBox>("PullRequestSelector")!.ItemCount).IsEqualTo(2);
        } finally {
            window.Close();
            await vm.TeardownAsync();
        }
    });

    [Test]
    public Task Unified_fix_action_keeps_the_draft_and_returns_to_the_native_composer() => RunOnUiAsync(async () => {
        using var preview = new TooltipPreviewScope();
        preview.Enable(ClassicAppearance.Dark);
        await using var scene = await DocsShotScenes.MainWindowAsync(arrange: main => {
            main.OpenSessionPullRequest("a1", AgentOrigin.Local, PullRequestWire.Subject(NorthwindPullRequestSource.Link));
            return Task.CompletedTask;
        });
        var window = scene.Window;
        window.Resources[PullRequestReaderHost.UnifiedReaderResource] = true;
        window.Show();
        var workspace = (WorkspaceViewModel)((MainWindowViewModel)window.DataContext!).CurrentWorkspace!;
        workspace.Chat!.ComposerText = "Keep my existing draft.";
        await WaitUntilAsync(() => workspace.PullRequests!.ShowsFix &&
            window.GetVisualDescendants().OfType<UnifiedPullRequestReader>().Any());
        TooltipPreviewScope.Pump(window);
        var reader = window.GetVisualDescendants().OfType<UnifiedPullRequestReader>().Single();
        var fix = reader.GetVisualDescendants().OfType<Button>().Single(button => Equals(button.Content, "Fix it"));
        await Assert.That(fix.Focus(NavigationMethod.Tab)).IsTrue();
        TooltipPreviewScope.Press(window, PhysicalKey.Enter);
        await Assert.That(workspace.ActiveTab).IsEqualTo(WorkspaceTab.Chat);
        await Assert.That(workspace.Chat.ComposerText).StartsWith("Keep my existing draft.");
        await Assert.That(workspace.Chat.ComposerText).Contains("Get pull request #88 ready to merge");
        await Assert.That(window.FocusManager!.GetFocusedElement()).IsTypeOf<TextBox>();

        var draft = workspace.Chat.ComposerText;
        ((MainWindowViewModel)window.DataContext!).OpenSessionPullRequest("a1", AgentOrigin.Local, PullRequestWire.Subject(NorthwindPullRequestSource.Link));
        await WaitUntilAsync(() => window.GetVisualDescendants().OfType<UnifiedPullRequestReader>().Any());
        window.Resources.Remove(PullRequestReaderHost.UnifiedReaderResource);
        TooltipPreviewScope.Pump(window);
        await Assert.That(window.GetVisualDescendants().OfType<PullRequestReader>().Any()).IsTrue();
        await Assert.That(workspace.Chat.ComposerText).IsEqualTo(draft);
        window.Resources[PullRequestReaderHost.UnifiedReaderResource] = true;
        TooltipPreviewScope.Pump(window);
        await Assert.That(window.GetVisualDescendants().OfType<UnifiedPullRequestReader>().Any()).IsTrue();
        await Assert.That(workspace.Chat.ComposerText).IsEqualTo(draft);
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
