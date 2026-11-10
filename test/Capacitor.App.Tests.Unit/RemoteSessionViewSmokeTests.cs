using System.Reactive.Linq;
using System.Reflection;
using Avalonia;
using Avalonia.Automation.Peers;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Platform;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Core.WorkItems;
using Capacitor.Remote.Models;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.RemoteFixtures;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// Headless rendering acceptance for the remote host: the card templates it shares with the chat
/// pane resolve here too, so a card raised on another machine is answerable in this window.
/// These run under the real Avalonia scheduler (AvaloniaSession.DispatchAsync, not the immediate
/// pin): SessionAccessService publishes from a thread-pool continuation, and only the real
/// scheduler marshals that onto the UI thread before it reaches a binding.
[NotInParallel(nameof(AvaloniaSession))]
public class RemoteSessionViewSmokeTests {
    [Test]
    public async Task An_unresolved_server_attempt_can_be_discarded_without_sending_a_prompt() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                var id = Guid.NewGuid();
                host.Lane.PendingInputSubject.OnNext(new("s1", [new QueuedInputItem { DispatchId = id, Text = "old attempt" }]));
                host.Settle();
                var discard = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "DiscardQueuedButton" && b.IsVisible);
                var point = discard.TranslatePoint(new Point(discard.Bounds.Width / 2, discard.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
                await host.SettleUntilAsync(() => host.Vm.Chat.QueuedMessages.Count == 0, "discarded");
                await Assert.That(host.Lane.UserInputs).IsEmpty();
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    [Test]
    public async Task Connecting_keeps_details_visible_without_competing_chat_waiting_messages() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host(row: AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", Status = "Running", DaemonName = "work-mac",
                Vendor = "claude", OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            }));
            try {
                host.Settle();
                await Assert.That(host.View.FindControl<WorkContextView>("WorkContextHost")!.IsEffectivelyVisible).IsTrue();
                await Assert.That(host.Vm.Chat.PhaseNote).IsEmpty();
                await Assert.That(host.Vm.Chat.ShowsTerminalOffer).IsFalse();
                var title = host.View.GetVisualDescendants().OfType<TextBlock>().Single(t => t.Text == "Starting session…" && t.IsEffectivelyVisible);
                await Assert.That(title.TranslatePoint(new Point(0, 0), host.Window)!.Value.Y).IsGreaterThan(180);
                var action = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Open Terminal"));
                host.Window.MouseMove(new Point(0, 0));
                host.Settle();
                var presenter = action.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                await Assert.That(presenter.Background).IsSameReferenceAs(host.View.FindResource("KcapSurfaceRaisedBrush"));
                CaptureLoading(host, "session-starting");
                var point = action.TranslatePoint(new Point(action.Bounds.Width / 2, action.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseMove(point);
                host.Settle();
                await Assert.That(presenter.Background).IsSameReferenceAs(host.View.FindResource("KcapBorderBrush"));
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
                await host.SettleUntilAsync(() => host.Vm.IsTerminalActive, "terminal action opened its pane");
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    [Test]
    public async Task Reconnecting_keeps_existing_chat_visible_and_uses_the_shared_notice() {
        await AvaloniaSession.DispatchAsync(async () => {
            var source = new FakeWorkContextSource();
            var assignment = new SessionWorkItemAssignmentDto { WorkItemId = "w1", Label = "WK-240", Source = "mcp", Confidence = 1, IsPrimary = true };
            source.Enqueue(new WorkContextRead(WorkContextReadKind.Ready, [assignment], assignment,
                new WorkItemDto { WorkItemId = "w1", Title = "Unify session connection states",
                    Key = new WorkItemKeyDto { ShortKey = "WK-240", Provider = "linear", Kind = "issue", Value = "WK-240" },
                    State = new WorkItemStateDto { Kind = "in_flight" } }, null,
                new SessionSummaryDto { SessionId = "s1" }, false, false, false, null));
            var retries = 0;
            using var host = new Host(workSource: source, retryConnection: () => { retries++; return Task.CompletedTask; },
                detail: Detail(Event(0, CanonicalEventTypes.UserMessageReceived,
                """{"content":"Review the session loading states"}"""), Event(1, CanonicalEventTypes.AssistantTextGenerated,
                """{"content":"I’ll check how chat and work details recover after a connection loss."}""")));
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready && host.Vm.Chat.Items.Count > 0 && host.Vm.WorkContext.IsReady, "chat and details loaded");
                var count = host.Vm.Chat.Items.Count;
                host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Offline, "connection dropped");
                await Assert.That(host.View.FindControl<ChatTabView>("ChatHost")!.IsEffectivelyVisible).IsTrue();
                await Assert.That(host.View.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Reconnecting to session…")).IsTrue();
                await Assert.That(host.Vm.Chat.Items.Count).IsEqualTo(count);
                await Assert.That(host.Vm.WorkContext.IsReady).IsTrue();
                CaptureLoading(host, "session-reconnecting");
                host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, Failure: Capacitor.Cli.Core.Http.ConnectionFailure.Refused));
                host.Settle();
                await Assert.That(host.View.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Couldn’t reconnect to this session")).IsTrue();
                CaptureLoading(host, "session-connection-failed");
                var retry = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Retry"));
                host.Window.MouseMove(new Point(0, 0));
                host.Settle();
                var presenter = retry.GetVisualDescendants().OfType<ContentPresenter>().First(p => p.Name == "PART_ContentPresenter");
                await Assert.That(presenter.Background).IsSameReferenceAs(host.View.FindResource("KcapSurfaceRaisedBrush"));
                retry.Focus(NavigationMethod.Tab);
                await Assert.That(retry.IsFocused).IsTrue();
                host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                host.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
                await host.SettleUntilAsync(() => retries == 1, "keyboard retry reached its callback");
                await Assert.That(retries).IsEqualTo(1);
                // Re-establishment must keep the transcript visible too, until the watch answers.
                var watch = new TaskCompletionSource<HubCallOutcome>();
                host.Lane.AccessWatchHandler = _ => watch.Task;
                host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Connecting, "re-establishing");
                await Assert.That(host.View.FindControl<ChatTabView>("ChatHost")!.IsEffectivelyVisible).IsTrue();
                watch.SetResult(HubCallOutcome.Ok);
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "recovered");
                CaptureLoading(host, "session-recovered");
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    /// A stopped local server is the cause a Retry cannot repair, so the notice offers its start instead.
    [Test]
    public async Task A_stopped_local_server_offers_start_server_instead_of_retry() {
        await AvaloniaSession.DispatchAsync(async () => {
            var starts = 0;
            using var host = new Host(localServer: lane => new LocalServerRecoveryViewModel(lane.Status, isLocalServer: true,
                _ => { starts++; return Task.FromResult(new Capacitor.Cli.Core.ProcessResult(0, "", "", false)); }, () => Task.CompletedTask, CancellationToken.None));
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "connected");
                host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying, Failure: Capacitor.Cli.Core.Http.ConnectionFailure.Refused));
                await host.SettleUntilAsync(() => host.Vm.ConnectionNotice?.Title == "Your kcap server is stopped", "stopped notice");
                var start = host.View.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && Equals(b.Content, "Start server"));
                await Assert.That(start.Classes.Contains("kcapPrimary")).IsTrue();
                Click(host, start);
                await host.SettleUntilAsync(() => starts == 1, "start requested");
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    [Test]
    public async Task Slow_context_shows_loading_then_failure_and_retry_restores_an_explained_empty_state() {
        await AvaloniaSession.DispatchAsync(async () => {
            var source = new FakeWorkContextSource();
            var gate = source.Gate();
            using var host = new Host(workSource: source);
            try {
                await host.SettleUntilAsync(() => source.InFlight == 1, "context read started");
                await Assert.That(host.View.FindControl<WorkContextView>("WorkContextHost")!.IsEffectivelyVisible).IsTrue();
                await Assert.That(host.Vm.WorkContext.Phase).IsEqualTo(WorkContextPhase.Loading);
                CaptureLoading(host, "work-context-loading");
                gate.SetResult(Capacitor.Cli.Core.WorkItems.WorkContextRead.Of(Capacitor.Cli.Core.WorkItems.WorkContextReadKind.Unreachable));
                await host.SettleUntilAsync(() => host.Vm.WorkContext.ShowsRetry, "context failure");
                await Assert.That(host.View.GetVisualDescendants().OfType<SessionConnectionNotice>()
                    .Any(n => n.IsEffectivelyVisible && n.DataContext is SessionConnectionPresentation { Title: "Waiting for chat…" })).IsTrue();
                CaptureLoading(host, "work-context-failed");
                source.Enqueue(Capacitor.Cli.Core.WorkItems.WorkContextRead.Of(Capacitor.Cli.Core.WorkItems.WorkContextReadKind.Ready));
                await host.Vm.WorkContext.RefreshCommand.Execute();
                await host.SettleUntilAsync(() => host.Vm.WorkContext.Phase == WorkContextPhase.NoWorkItem, "context retry");
                await Assert.That(host.Vm.WorkContext.PhaseNote).IsNotEmpty();
                CaptureLoading(host, "work-context-recovered");
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    static void CaptureLoading(Host host, string name) {
        if (Environment.GetEnvironmentVariable("KCAP_SHOTS_OUT") is not { } dir) return;
        host.Window.SetRenderScaling(2);
        host.Window.FocusManager?.Focus(null);
        host.Settle();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        host.Window.CaptureRenderedFrame()!.Save(Path.Join(dir, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }

    sealed class Host : IDisposable {
        readonly SessionAccessService _access;
        readonly FakeAgentDirectory _directory = new();
        readonly LocalServerRecoveryViewModel? _localServer;

        public readonly FakeServerLane Lane = new();
        public readonly FakePermissionService Permissions = new();
        public readonly FakeTimeProvider Time = new();
        public readonly FakeRemoteAgents Remote = new();
        public readonly RemoteSessionViewModel Vm;
        public readonly RemoteSessionView View;
        public readonly Window Window;

        public Host(IPlanArtifactSource? planArtifacts = null, string sessionId = "s1", IWindowImpl? windowImpl = null, bool realTerminal = false,
                AgentRow? row = null, Func<AgentRow, HandoffComposerViewModel>? handoff = null, FakeWorkContextSource? workSource = null, SessionDetailDto? detail = null, Func<Task>? retryConnection = null,
                Func<FakeServerLane, LocalServerRecoveryViewModel>? localServer = null) {
            _access = new SessionAccessService(Lane, Time);
            Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 1));
            row ??= AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = sessionId, Status = "Running", DaemonName = "work-mac",
                Vendor = "claude", OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
            });
            _directory.Rows.AddOrUpdate(row);
            _localServer = localServer?.Invoke(Lane);
            Vm = new RemoteSessionViewModel(row, _directory, _access, Permissions, NewActions(), Lane,
                (_, _) => Task.FromResult(new SessionDetailFetch(detail ?? Detail())), new RecordingOpener(), Time, workSource ?? new FakeWorkContextSource(),
                () => realTerminal ? new XtermTerminalSurface(130, 54) : new FakeTerminalSurface(), planArtifacts,
                remoteAgents: Remote, notifier: new RecordingNotifier(), handoff: handoff, retryConnection: retryConnection,
                localServer: _localServer);
            View = new RemoteSessionView { DataContext = Vm };
            Window = windowImpl is null ? new Window() : new Window(windowImpl);
            Window.Content = View;
            Window.Width = 900;
            Window.Height = 700;
            Window.Show();
            Settle();
        }

        public void Settle() {
            Dispatcher.UIThread.RunJobs();
            Window.UpdateLayout();
            Dispatcher.UIThread.RunJobs();
        }

        public async Task SettleUntilAsync(Func<bool> condition, string what) {
            await WaitUntilAsync(() => { Settle(); return condition(); }, what: what);
            Settle();
        }

        public void SetStatus(string status) {
            var row = _directory.Rows.Items.Single();
            _directory.Rows.AddOrUpdate(row with { Status = status });
            Settle();
        }

        public void Dispose() {
            Window.Close();
            _localServer?.Dispose();
            _access.Dispose();
            Permissions.Dispose();
            _directory.Dispose();
            Remote.Dispose();
        }
    }

    // ── the ended session's hand-off composer ──────────────────────────────────────────────────

    static AgentRow EndedServerRow() => AgentRow.FromServer(new RepoSessionDto {
        SessionId = "s1", Status = "ended", AccessLevel = "full", Vendor = "claude", Title = "Widgets",
        Owner = new() { UserId = "u1", DisplayName = "Alexey" }, Branch = "feature/x",
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
        EndedAt = DateTimeOffset.UnixEpoch.AddHours(1),
        Repo = new() { Hash = "h", Owner = "acme", Name = "widgets" },
    }, hostedPresentation: false);

    const string Machine = "machine-1";

    static AgentRow ResumableServerRow() => AgentRow.FromServer(new RepoSessionDto {
        SessionId = "s1", Status = "ended", AccessLevel = "full", Vendor = "claude", Title = "Widgets",
        Owner = new() { UserId = "u1", DisplayName = "Alexey" }, Branch = "feature/x", Cwd = HandoffComposerFixture.Checkout,
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch.AddHours(1),
        EndedAt = DateTimeOffset.UnixEpoch.AddHours(1),
        Repo = new() { Hash = "h", Owner = "acme", Name = "widgets" },
        Recording = new() { MachineId = Machine, ConfigDir = "/Users/me/.claude" },
    }, hostedPresentation: false);

    /// Runs the body over an ended server session whose composer is shown, settled until the
    /// composer has answered whether it can start.
    static Task WithHandoffAsync(Func<Host, HandoffComposerFixture, HandoffComposerView, Task> body,
            Action<HandoffComposerFixture>? arrange = null, AgentRow? row = null) =>
        AvaloniaSession.DispatchAsync(async () => {
            using var fixture = new HandoffComposerFixture();
            arrange?.Invoke(fixture);
            using var host = new Host(row: row ?? EndedServerRow(), handoff: fixture.Build);
            try {
                await host.SettleUntilAsync(() => host.Vm.Chat.Handoff is { } composer
                    && (composer is { IsProbing: false } && (composer.ProbedStart is not null || composer.DisabledReason is not null)
                     || composer.Progress == HandoffComposerViewModel.WaitingForDaemon), "the hand-off checked");
                var view = host.View.GetVisualDescendants().OfType<HandoffComposerView>().Single(v => v.IsEffectivelyVisible);
                await body(host, fixture, view);
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });

    static T Named<T>(Control root, string name) where T : Control =>
        root.GetVisualDescendants().OfType<T>().Single(c => c.Name == name);

    /// Real pointer input: a Button's Command runs off its own click handling, which a raised
    /// ClickEvent never reaches.
    static void Click(Host host, Control target) {
        host.Settle();
        var point = target.TranslatePoint(new Point(target.Bounds.Width / 2, target.Bounds.Height / 2), host.Window)
            ?? throw new InvalidOperationException("Click target is not under the window.");
        host.Window.MouseDown(point, MouseButton.Left);
        host.Window.MouseUp(point, MouseButton.Left);
        host.Settle();
    }

    [Test]
    public Task Typing_and_clicking_start_launches() => WithHandoffAsync(async (host, fixture, view) => {
        var input = Named<TextBox>(view, "HandoffInput");
        input.Focus();
        host.Settle();
        host.Window.KeyTextInput("carry on");
        host.Settle();

        Click(host, Named<Button>(view, "StartHandoffButton"));
        await host.SettleUntilAsync(() => fixture.Launch.Requests.Count == 1, "the launch");

        var request = fixture.Launch.Requests.Single();
        await Assert.That(request.Prompt).IsEqualTo("carry on");
        await Assert.That(request.RepoPath).IsEqualTo(HandoffComposerFixture.Checkout);
        await Assert.That(request.Handoff!.SourceSessionId).IsEqualTo("s1");
    });

    [Test]
    public Task Clicking_resume_launches_it_in_place() => WithHandoffAsync(async (host, fixture, view) => {
        await host.SettleUntilAsync(() => host.Vm.Chat.Handoff!.ResumeOffered, "resume offered");
        await Assert.That(Named<StackPanel>(view, "HandoffStrip").IsEffectivelyVisible).IsFalse();

        Click(host, Named<Button>(view, "ResumeButton"));
        await host.SettleUntilAsync(() => fixture.Launch.Requests.Count == 1, "the launch");

        await Assert.That(fixture.Launch.Requests.Single().Resume).IsEqualTo(new ResumeLaunchPayload("s1", HandoffComposerFixture.Checkout));
    }, fixture => fixture.OfferResume(Machine), ResumableServerRow());

    [Test]
    public Task Hand_off_opens_the_settings_and_Esc_closes_them() => WithHandoffAsync(async (host, fixture, view) => {
        await host.SettleUntilAsync(() => host.Vm.Chat.Handoff!.ResumeOffered, "resume offered");

        Click(host, Named<Button>(view, "HandoffButton"));
        await Assert.That(Named<StackPanel>(view, "HandoffStrip").IsEffectivelyVisible).IsTrue();
        await Assert.That(Named<Button>(view, "ResumeButton").IsEffectivelyVisible).IsFalse();
        await Assert.That(Named<Button>(view, "CancelHandoffButton").IsEffectivelyVisible).IsTrue();

        var input = Named<TextBox>(view, "HandoffInput");
        input.Focus();
        host.Settle();
        host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
        host.Settle();

        await Assert.That(Named<StackPanel>(view, "HandoffStrip").IsEffectivelyVisible).IsFalse();
        await Assert.That(Named<Button>(view, "ResumeButton").IsEffectivelyVisible).IsTrue();
        await Assert.That(fixture.Launch.Requests).IsEmpty();
    }, fixture => fixture.OfferResume(Machine), ResumableServerRow());

    [Test]
    public Task Dropping_a_file_adds_it_to_the_tray() => WithHandoffAsync(async (host, fixture, view) => {
        var composer = host.Vm.Chat.Handoff!;
        await Assert.That(composer.CanAttach).IsTrue();
        var card = Named<Border>(view, "HandoffCard");
        var transfer = new DataTransfer();
        var item = new DataTransferItem();
        item.SetFile(FakeStorageFile.Of("dropped.png", new byte[] { 1, 2, 3, 4 }));
        transfer.Add(item);

        card.RaiseEvent(new DragEventArgs(DragDrop.DragOverEvent, transfer, card, new Point(6, 6), KeyModifiers.None));
        host.Settle();
        await Assert.That(card.Classes.Contains("dragOver")).IsTrue();

        card.RaiseEvent(new DragEventArgs(DragDrop.DropEvent, transfer, card, new Point(6, 6), KeyModifiers.None));
        await (view.PendingIntakeForTesting ?? Task.CompletedTask);
        host.Settle();

        await Assert.That(composer.Tray.Items.Select(f => f.FileName)).IsEquivalentTo(["dropped.png"]);
        await Assert.That(Named<AttachmentChipStrip>(view, "HandoffChipStrip").IsEffectivelyVisible).IsTrue();
        // The chat's own composer is hidden, so the drop reached the hand-off card and nothing else.
        await Assert.That(host.Vm.Chat.Tray.Items).IsEmpty();
    });

    [Test]
    public Task Enter_in_the_box_does_not_launch() => WithHandoffAsync(async (host, fixture, view) => {
        var input = Named<TextBox>(view, "HandoffInput");
        input.Focus();
        host.Settle();
        host.Window.KeyTextInput("line one");
        host.Window.KeyPressQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        host.Window.KeyReleaseQwerty(PhysicalKey.Enter, RawInputModifiers.None);
        host.Window.KeyTextInput("line two");
        host.Settle();
        await Task.Delay(50);
        host.Settle();

        await Assert.That(fixture.Launch.Requests).IsEmpty();
        await Assert.That(host.Vm.Chat.Handoff!.Text.ReplaceLineEndings("\n")).IsEqualTo("line one\nline two");
    });

    [Test]
    public Task The_agent_chip_flyout_opens_from_the_strip() => WithHandoffAsync(async (host, _, view) => {
        var chips = view.GetVisualDescendants().OfType<LaunchChipsView>().Single();
        Click(host, Named<Button>(chips, "AgentChip"));

        var menu = chips.OpenMenuForTesting;
        await Assert.That(menu).IsNotNull();
        await Assert.That(menu!.IsOpen).IsTrue();
        menu.Hide();
        host.Settle();
    });

    [Test]
    public Task A_disabled_handoff_shows_its_reason_and_no_start() => WithHandoffAsync(async (host, _, view) => {
        var notice = Named<TextBlock>(view, "HandoffNotice");
        await Assert.That(notice.Text).IsEqualTo(HandoffComposerViewModel.ServerTooOld);
        await Assert.That(notice.IsEffectivelyVisible).IsTrue();
        await Assert.That(Named<Button>(view, "StartHandoffButton").IsEffectivelyVisible).IsFalse();
        await Assert.That(host.Vm.Chat.Handoff!.StripLabel).IsEqualTo("");
    }, arrange: fixture => fixture.Launch.Features = System.Collections.Frozen.FrozenSet<string>.Empty);

    [Test]
    public Task A_refusal_and_the_reprobes_reason_both_show() => WithHandoffAsync(async (host, fixture, view) => {
        fixture.Launch.Next = new LaunchOutcome(false, null, "handoff_start_changed: feature/x moved");
        fixture.Ops.Answer = request => request.LocalOnly
            ? new HandoffProbeReplyDto(HandoffRefusals.InvalidTarget, null, null, null, null)
            : FakeHandoffProbeOps.Reply(FakeHandoffProbeOps.AttachedStart);

        Click(host, Named<Button>(view, "StartHandoffButton"));
        await host.SettleUntilAsync(() => host.Vm.Chat.Handoff!.DisabledReason is not null, "the re-probe");

        var error = Named<TextBlock>(view, "HandoffErrorText");
        await Assert.That(error.Text).IsEqualTo(HandoffFailureText.StartChanged);
        await Assert.That(error.IsEffectivelyVisible).IsTrue();
        var notice = Named<TextBlock>(view, "HandoffNotice");
        await Assert.That(notice.Text).IsEqualTo(HandoffComposerViewModel.InvalidTarget);
        await Assert.That(notice.IsEffectivelyVisible).IsTrue();
    });

    [Test]
    public Task An_unreadable_origin_shows_one_line_in_the_banner() => WithHandoffAsync(async (host, fixture, view) => {
        fixture.Ops.Answer = request => request.LocalOnly
            ? FakeHandoffProbeOps.Reply(FakeHandoffProbeOps.AttachedStart)
            : new HandoffProbeReplyDto(null,
                "fatal: Cannot prompt because user interactivity has been disabled.\n"
              + "fatal: could not read Username for 'https://github.com': terminal prompts disabled", null, null, null);

        Click(host, Named<Button>(view, "StartHandoffButton"));
        await host.SettleUntilAsync(() => host.Vm.Chat.Handoff!.Error is not null, "the read of origin");

        var error = Named<TextBlock>(view, "HandoffErrorText");
        await Assert.That(error.Text).IsEqualTo(HandoffComposerViewModel.OriginSignInNeeded);
        await Assert.That(error.IsEffectivelyVisible).IsTrue();
        await Assert.That(Named<Button>(view, "StartHandoffButton").IsEffectivelyVisible).IsTrue();
        await Assert.That(fixture.Launch.Requests).IsEmpty();
    });

    [Test]
    public Task A_connecting_daemon_shows_why_start_waits() => WithHandoffAsync(async (host, _, view) => {
        var progress = Named<TextBlock>(view, "HandoffProgress");
        await Assert.That(progress.Text).IsEqualTo(HandoffComposerViewModel.WaitingForDaemon);
        await Assert.That(progress.IsEffectivelyVisible).IsTrue();
    }, arrange: fixture => fixture.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null)));

    [Test]
    public async Task A_remote_terminal_scrolls_locally_and_sends_keyboard_and_button_arrows() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host(realTerminal: true);
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                await host.Vm.ShowTerminalCommand.Execute();
                await host.SettleUntilAsync(() => host.Vm.Terminal!.Phase == RemoteTerminalPhase.Live, "live terminal");
                host.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 130, 54));
                var feed = string.Concat(Enumerable.Range(0, 120).Select(i => $"line {i}\r\n"))
                    + "prompt\x1b[?1h";
                host.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(feed))));
                host.Window.Height = 450;
                host.Settle();

                var terminal = host.View.FindControl<RemoteTerminalControl>("TerminalHost")!;
                var viewport = host.View.FindControl<RemoteTerminalViewport>("TerminalViewport")!;
                var model = terminal.Model!;
                await Assert.That(viewport.Extent.Height).IsGreaterThan(viewport.Viewport.Height);
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);
                var point = viewport.TranslatePoint(new Point(30, 30), host.Window)!.Value;
                var beforeWheel = viewport.Offset.Y;
                host.Window.MouseWheel(point, new Vector(0, 0.1));
                host.Settle();
                await Assert.That(viewport.Offset.Y).IsLessThan(beforeWheel);
                var afterSmallWheel = viewport.Offset.Y;
                host.Window.MouseWheel(point, new Vector(0, 100));
                host.Settle();
                await Assert.That(afterSmallWheel - viewport.Offset.Y).IsLessThanOrEqualTo(9 * terminal.CellHeight);
                for (var i = 0; i < 40; i++) { host.Window.MouseWheel(point, new Vector(0, 100)); host.Settle(); }
                await Assert.That(viewport.Offset.Y).IsEqualTo(0);
                await Assert.That(model.ScrollOffset).IsLessThan(model.MaxScrollback);
                var beforeLargeWheel = model.ScrollOffset;
                host.Window.MouseWheel(point, new Vector(0, -100));
                host.Settle();
                await Assert.That(model.ScrollOffset - beforeLargeWheel).IsLessThanOrEqualTo(9);
                for (var i = 0; i < 40; i++) { host.Window.MouseWheel(point, new Vector(0, -100)); host.Settle(); }
                await Assert.That(model.ScrollOffset).IsEqualTo(model.MaxScrollback);
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);

                host.Window.MouseWheel(point, new Vector(0, 100));
                host.Settle();
                terminal.Focus();
                host.Window.KeyPress(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                host.Window.KeyRelease(Key.Up, RawInputModifiers.None, PhysicalKey.ArrowUp, null);
                host.Window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                host.Window.KeyRelease(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 2, "keyboard arrows");
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);
                foreach (var label in new[] { "↑", "↓" }) {
                    host.Window.MouseWheel(point, new Vector(0, 100));
                    host.Settle();
                    var button = host.View.GetVisualDescendants().OfType<Button>().Single(b => Equals(b.Content, label));
                    var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), host.Window)!.Value;
                    host.Window.MouseDown(center, MouseButton.Left);
                    host.Window.MouseUp(center, MouseButton.Left);
                    host.Settle();
                }
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 4, "button arrows");
                await Assert.That(terminal.IsKeyboardFocusWithin).IsTrue();
                await Assert.That(viewport.Offset.Y).IsGreaterThan(0);
                await Assert.That(host.Lane.SpecialKeys).IsEquivalentTo(new[] {
                    ("a1", SpecialKeys.ArrowUp), ("a1", SpecialKeys.ArrowDown),
                    ("a1", SpecialKeys.ArrowUp), ("a1", SpecialKeys.ArrowDown),
                });
                await Assert.That((model.Terminal.Cols, model.Terminal.Rows)).IsEqualTo((130, 54));
            } finally {
                await host.Vm.TeardownAsync();
            }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task A_mouse_tracking_app_receives_proportional_wheel_input_in_either_buffer(bool alternate) {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host(realTerminal: true);
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                await host.Vm.ShowTerminalCommand.Execute();
                await host.SettleUntilAsync(() => host.Vm.Terminal!.Phase == RemoteTerminalPhase.Live, "live terminal");
                host.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 103, 38));
                var feed = string.Concat(Enumerable.Range(0, 90).Select(i => $"redraw {i}\r\n"));
                feed += (alternate ? "\x1b[?1049h" : "") + "\x1b[?1000h\x1b[?1006hfull-screen conversation";
                var bytes = System.Text.Encoding.UTF8.GetBytes(feed);
                host.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1", Convert.ToBase64String(bytes)));
                host.Settle();
                var viewport = host.View.FindControl<RemoteTerminalViewport>("TerminalViewport")!;
                var terminal = host.View.FindControl<RemoteTerminalControl>("TerminalHost")!;
                var scrollback = terminal.Model!.ScrollOffset;
                var point = terminal.TranslatePoint(new Point(30, 30), host.Window)!.Value;
                for (var i = 0; i < 10; i++) host.Window.MouseWheel(point, new Vector(0, 0.1));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 1, "one accumulated wheel step");
                host.Window.MouseWheel(point, new Vector(0, 100.25));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 7, "a large event is capped at six reports");
                host.Window.MouseWheel(point, new Vector(0, 0.75));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 8, "fractional remainder survives the cap");
                await Assert.That(viewport.HorizontalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Hidden);
                await Assert.That(viewport.VerticalScrollBarVisibility).IsEqualTo(ScrollBarVisibility.Hidden);
                foreach (var (_, key) in host.Lane.SpecialKeys) {
                    await Assert.That(TerminalScrollKey.TryDecode(key, out var report)).IsTrue();
                    await Assert.That(report.AsSpan().StartsWith("\x1b[<64;"u8)).IsTrue();
                }
                await Assert.That(terminal.Model.ScrollOffset).IsEqualTo(scrollback);
                host.Window.Height = 450;
                host.Lane.TerminalDimensionsSubject.OnNext(new TerminalSize("a1", 100, 30));
                host.Settle();
                host.Lane.TerminalOutputSubject.OnNext(new TerminalOutputFrame("a1",
                    Convert.ToBase64String("\x1b[2J\x1b[Hredrawn history\x1b[30;1Hdraft"u8.ToArray())));
                host.Settle();
                // Resizing scrolls the terminal top out of view; aim inside the visible viewport
                point = viewport.TranslatePoint(new Point(30, 30), host.Window)!.Value;
                var before = terminal.Model.ScrollOffset;
                host.Window.MouseWheel(point, new Vector(0, -0.5));
                host.Window.MouseWheel(point, new Vector(0, -0.5));
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 9, "one downward step after resize");
                await Assert.That(TerminalScrollKey.TryDecode(host.Lane.SpecialKeys[^1].Key, out var down)).IsTrue();
                await Assert.That(down.AsSpan().StartsWith("\x1b[<65;"u8)).IsTrue();
                await Assert.That(terminal.Model.ScrollOffset).IsEqualTo(before);
                var buffer = terminal.Model.Terminal.Buffer;
                await Assert.That(buffer.GetLine(buffer.BaseY)!.TranslateToString(true)).IsEqualTo("redrawn history");
                await Assert.That(buffer.GetLine(buffer.BaseY + 29)!.TranslateToString(true)).IsEqualTo("draft");
            } finally {
                await host.Vm.TeardownAsync();
            }
        });
    }

    // Forward to the real headless platform so these assertions exercise hit testing and routed
    // input, while recording the move request that a native window would receive.
    public class MoveRecorder : DispatchProxy {
        public IWindowImpl Target { get; set; } = null!;
        public int Moves { get; private set; }

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args) {
            if (targetMethod!.Name == "BeginMoveDrag") Moves++;
            return targetMethod.Invoke(Target, args);
        }
    }

    [Test]
    public async Task The_remote_header_drags_beside_work_context_and_keeps_tab_buttons_clickable() {
        await AvaloniaSession.DispatchAsync(async () => {
            var platformWindow = new Window();
            var platform = DispatchProxy.Create<IWindowImpl, MoveRecorder>();
            var recorder = (MoveRecorder)platform;
            recorder.Target = platformWindow.PlatformImpl!;
            var closed = typeof(ITopLevelImpl).GetProperty("Closed", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)!;
            var helperClosed = closed.GetValue(recorder.Target);
            try {
                using var host = new Host(windowImpl: platform);
                try {
                    await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                    await Assert.That(host.View.GetVisualDescendants().OfType<WorkContextView>().Single().DataContext)
                    .IsSameReferenceAs(host.Vm.WorkContext);

                    // Blank padding needs a hit-testable background; title text must bubble too.
                    var title = host.View.FindControl<SessionTitleEditor>("RemoteTitle")!.FindControl<TextBlock>("TitleText")!;
                    var titlePoint = title.TranslatePoint(new Point(4, title.Bounds.Height / 2), host.Window)!.Value;
                    foreach (var point in new[] { new Point(200, 8), titlePoint }) {
                        platformWindow.MouseDown(point, MouseButton.Left);
                        platformWindow.MouseUp(point, MouseButton.Left);
                    }
                    await Assert.That(recorder.Moves).IsEqualTo(2);

                    platformWindow.MouseDown(new Point(200, 8), MouseButton.Right);
                    platformWindow.MouseUp(new Point(200, 8), MouseButton.Right);
                    await Assert.That(recorder.Moves).IsEqualTo(2);

                    var terminal = host.View.FindControl<Button>("TerminalTabButton")!;
                    var buttonPoint = terminal.TranslatePoint(new Point(terminal.Bounds.Width / 2, terminal.Bounds.Height / 2), host.Window)!.Value;
                    platformWindow.MouseDown(buttonPoint, MouseButton.Left);
                    platformWindow.MouseUp(buttonPoint, MouseButton.Left);
                    host.Settle();
                    await Assert.That(host.Vm.IsTerminalActive).IsTrue();
                    await Assert.That(recorder.Moves).IsEqualTo(2);
                } finally {
                    await host.Vm.TeardownAsync();
                }
            } finally {
                // Host replaces the shared implementation's close callback. Restore the helper's
                // callback after Host closes so its managed subscriptions are released too.
                closed.SetValue(recorder.Target, helperClosed);
                platformWindow.Close();
            }
            await Assert.That(platformWindow.PlatformImpl).IsNull();
            return true;
        });
    }

    [Test]
    public async Task An_acp_question_renders_in_the_chat_pane_through_the_shared_card_template() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(chat.IsVisible).IsTrue();

            var card = PendingPermissionRequest.FromServer(
                new ServerElicitationRequest("s1", "q1", "Pick a branch",
                    [new() { OptionId = "a", Label = "main" }, new() { OptionId = "b", Label = "next" }], false),
                DateTimeOffset.UtcNow);
            card.AgentId = "a1";
            host.Permissions.Add(card);
            await host.SettleUntilAsync(() => host.Vm.Chat.Cards.HasPendingCards, "the card");

            await Assert.That(chat.GetVisualDescendants().OfType<Border>().Any(b => b.Name == "AcpQuestionCard")).IsTrue();
            var options = chat.GetVisualDescendants().OfType<Button>()
                .Where(b => b is not ToggleButton && b.Classes.Contains("acpOption"))
                .ToList();
            var labels = options.Select(b => ((TextBlock)b.Content!).Text ?? "").ToList();
            await Assert.That(labels).IsEquivalentTo(new[] { "main", "next" });
            // A long option has to wrap in the stretched row rather than run off its edge.
            await Assert.That(options.Select(b => ((TextBlock)b.Content!).TextWrapping).Distinct())
                .IsEquivalentTo(new[] { TextWrapping.Wrap });

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    /// Denied is a stated verdict, not an empty pane: the chat goes away and the banner says why.
    [Test]
    public async Task A_denied_session_replaces_the_chat_with_the_access_note() {
        await AvaloniaSession.DispatchAsync(async () => {
            const string repository = "/private/PRIVATE_REPOSITORY_SENTINEL/repo";
            const string worktree = "/private/PRIVATE_WORKTREE_SENTINEL/checkout";
            const string branch = "PRIVATE_BRANCH_SENTINEL";
            using var host = new Host(row: AgentRow.FromRemote(new AgentInstanceDto {
                AgentId = "a1", SessionId = "s1", Status = "Running", DaemonName = "work-mac",
                Vendor = "claude", OwnerUserId = "u1", RegisteredAt = DateTime.UtcNow,
                RepoPath = repository,
            }) with {
                WorktreePath = worktree, Branch = branch,
                WorkLocation = "borrowed", BorrowedFrom = "PRIVATE_BORROWED_SENTINEL",
            });
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");

            var banner = host.View.FindControl<Border>("AccessBanner")!;
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            var context = host.View.FindControl<WorkContextView>("WorkContextHost")!;
            if (!host.Vm.WorkContext.CheckoutExpanded) await host.Vm.WorkContext.ToggleCheckoutCommand.Execute();
            host.Settle();
            var facts = context.FindControl<Grid>("CheckoutFacts")!;
            string AccessibleCheckout() => string.Join('\n', context.GetVisualDescendants().OfType<Control>()
                .Where(c => c.IsEffectivelyVisible).Select(c => ControlAutomationPeer.CreatePeerForElement(c).GetName()));
            await Assert.That(banner.IsVisible).IsFalse();
            await Assert.That(chat.IsVisible).IsTrue();
            await Assert.That(facts.IsEffectivelyVisible).IsTrue();
            await Assert.That(AccessibleCheckout()).Contains(branch);

            host.Lane.AccessWatchHandler = _ => Task.FromResult(HubCallOutcome.Denied("Session not visible to caller"));
            host.Lane.SessionAccessChangedSubject.OnNext("s1");
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Denied, "denied");
            await Assert.That(context.IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Vm.WorkContext.RepositoryPath).IsNull();
            await Assert.That(host.Vm.WorkContext.WorktreePath).IsNull();
            await Assert.That(host.Vm.WorkContext.Branch).DoesNotContain(branch);

            await Assert.That(chat.IsVisible).IsFalse();
            await Assert.That(banner.IsVisible).IsTrue();
            await Assert.That(host.View.FindControl<TextBlock>("AccessNoteText")!.Text)
                .IsEqualTo("You no longer have access to this session");

            var watch = new TaskCompletionSource<HubCallOutcome>();
            host.Lane.AccessWatchHandler = _ => watch.Task;
            host.Vm.RecheckAccess();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Connecting, "access recheck");
            await Assert.That(chat.IsVisible).IsFalse().Because("a refusal must stand until access is established again");
            await Assert.That(context.IsEffectivelyVisible).IsFalse();
            await Assert.That(facts.IsEffectivelyVisible).IsFalse();
            await Assert.That(host.Vm.WorkContext.CheckoutExpanded).IsTrue();
            await Assert.That(AccessibleCheckout()).DoesNotContain("PRIVATE_");
            await Assert.That(host.Vm.WorkContext.RepositoryPath).IsNull();
            await Assert.That(host.Vm.WorkContext.WorktreePath).IsNull();
            await Assert.That(host.Vm.WorkContext.Branch).DoesNotContain(branch);

            host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Denied, "refusal retained while offline");
            watch.SetResult(HubCallOutcome.Ok);
            host.Settle();
            await Assert.That(context.IsEffectivelyVisible).IsFalse();
            await Assert.That(AccessibleCheckout()).DoesNotContain("PRIVATE_");

            var restored = new TaskCompletionSource<HubCallOutcome>();
            host.Lane.AccessWatchHandler = _ => restored.Task;
            host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Connected, Subject: "u1", Epoch: 2));
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Connecting, "restoring access");
            await Assert.That(context.IsEffectivelyVisible).IsFalse();
            restored.SetResult(HubCallOutcome.Ok);
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "access restored");
            await Assert.That(context.IsEffectivelyVisible).IsTrue();
            await Assert.That(facts.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.Vm.WorkContext.RepositoryPath).IsEqualTo(repository);
            await Assert.That(host.Vm.WorkContext.WorktreePath).IsEqualTo(worktree);
            await Assert.That(host.Vm.WorkContext.Branch).IsEqualTo(branch);
            await Assert.That(AccessibleCheckout()).Contains(branch);
            await host.Vm.TeardownAsync();
            return true;
        });
    }

    [Test]
    public async Task A_remote_running_turn_without_turn_details_can_be_interrupted_explicitly_until_idle_or_disconnected() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            try {
                await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
                var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
                var composer = chat.FindControl<TextBox>("ComposerInput")!;
                var interrupt = chat.FindControl<Button>("InterruptButton")!;
                await Assert.That(host.Vm.Chat.AgentStatus.IsRunningUnknownTurn).IsTrue();
                await Assert.That(interrupt.IsEffectivelyVisible).IsTrue();
                await Assert.That(interrupt.IsEffectivelyEnabled).IsTrue();

                composer.Focus();
                host.Window.KeyTextInput("keep this draft");
                host.Settle();
                // Unknown turn state keeps the explicit action, but a casual Escape must not reach the PTY.
                host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                host.Settle();
                await Assert.That(host.Lane.SpecialKeys).IsEmpty();
                await Assert.That(interrupt.Content).IsEqualTo("Interrupt");
                var point = interrupt.TranslatePoint(new Point(interrupt.Bounds.Width / 2, interrupt.Bounds.Height / 2), host.Window)!.Value;
                host.Window.MouseDown(point, MouseButton.Left);
                host.Window.MouseUp(point, MouseButton.Left);
                await host.SettleUntilAsync(() => host.Lane.SpecialKeys.Count == 1, "interrupt delivered");
                await Assert.That(host.Lane.SpecialKeys).IsEquivalentTo(new[] { ("a1", SpecialKeys.Escape) });
                await Assert.That(composer.Text).IsEqualTo("keep this draft");
                await Assert.That(host.Vm.Chat.ComposerText).IsEqualTo("keep this draft");

                host.SetStatus("Idle");
                await Assert.That(interrupt.IsVisible).IsFalse();
                await Assert.That(await host.Vm.Chat.InterruptCommand.CanExecute.FirstAsync()).IsFalse();
                composer.Focus();
                host.Window.KeyPressQwerty(PhysicalKey.Escape, RawInputModifiers.None);
                host.Settle();
                await Assert.That(host.Lane.SpecialKeys).Count().IsEqualTo(1);

                host.SetStatus("Running");
                await Assert.That(interrupt.IsEffectivelyVisible).IsTrue();
                await Assert.That(await host.Vm.Chat.InterruptCommand.CanExecute.FirstAsync()).IsTrue();
                host.Lane.StatusSubject.OnNext(new ServerLaneStatus(ServerLaneState.Retrying));
                host.Settle();
                await Assert.That(host.Vm.Chat.ShowsInterrupt).IsFalse();
                await Assert.That(await host.Vm.Chat.InterruptCommand.CanExecute.FirstAsync()).IsFalse();
                await Assert.That(host.Lane.SpecialKeys).Count().IsEqualTo(1);
                await Assert.That(host.Vm.Chat.ComposerText).IsEqualTo("keep this draft");
            } finally { await host.Vm.TeardownAsync(); }
            return true;
        });
    }

    /// The tab strip swaps the panes, and the terminal tab is offered only for a PTY harness.
    [Test]
    public async Task The_terminal_tab_swaps_the_pane_and_offers_the_special_keys() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            var terminalTab = host.View.FindControl<Button>("TerminalTabButton")!;
            var terminalHost = host.View.FindControl<Control>("TerminalPane")!;
            var chat = host.View.FindControl<ChatTabView>("ChatHost")!;
            await Assert.That(terminalTab.IsVisible).IsTrue();
            await Assert.That(terminalHost.IsVisible).IsFalse();

            await host.Vm.ShowTerminalCommand.Execute();
            await host.SettleUntilAsync(() => terminalHost.IsVisible, "the terminal pane");
            await Assert.That(chat.IsVisible).IsFalse();
            var keys = host.View.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("specialKey")).Select(b => b.Content as string).ToList();
            await Assert.That(keys).Contains("Esc");
            await Assert.That(keys).Contains("Ctrl+C");

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    [Test]
    public async Task The_artefacts_tab_hosts_the_list_and_reader_and_open_in_web_is_an_icon() {
        await AvaloniaSession.DispatchAsync(async () => {
            var source = new FakePlanArtifactSource();
            source.Default = Ready(Doc("docs/x-design.md"));
            using var host = new Host(source, DocumentSession);
            await host.SettleUntilAsync(() => host.Vm.Access == RemoteSessionAccess.Ready, "ready");
            await (host.Vm.Artefacts.PendingReadForTesting ?? Task.CompletedTask);
            host.Settle();
            var tab = host.View.FindControl<Button>("ArtefactsTabButton")!;
            await Assert.That(tab.IsEffectivelyVisible).IsTrue();
            await Assert.That(host.View.FindControl<ContentControl>("ArtefactsPane")!.Content).IsNull();

            await host.Vm.ShowArtefactsCommand.Execute();
            host.Settle();
            await Assert.That(host.View.FindControl<ContentControl>("ArtefactsPane")!.Content).IsTypeOf<ArtefactsView>();
            var open = host.View.FindControl<Button>("OpenInWebButton")!;
            await Assert.That(ToolTip.GetTip(open)).IsEqualTo("Open in web");
            await Assert.That(open.Content).IsNotTypeOf<string>();

            await host.Vm.TeardownAsync();
            return true;
        });
    }

    /// The owner's header offers the pencil; it swaps the title for the field and the ✓ / ✗ pair,
    /// and a save lands the server's title back in the header.
    [Test]
    public async Task The_pencil_toggles_the_editor_and_a_save_shows_the_new_title() {
        await AvaloniaSession.DispatchAsync(async () => {
            using var host = new Host();
            host.Remote.Rename = (_, _, title) => Task.FromResult(new SessionRenameResult(true, title, null));
            var editor = host.View.FindControl<SessionTitleEditor>("RemoteTitle")!;
            var pencil = editor.FindControl<Button>("RenameButton")!;
            var input = editor.FindControl<TextBox>("TitleInput")!;
            await Assert.That(pencil.IsEffectivelyVisible).IsTrue();
            await Assert.That(input.IsEffectivelyVisible).IsFalse();

            pencil.Command!.Execute(null);
            await host.SettleUntilAsync(() => input.IsEffectivelyVisible, "the field");
            await Assert.That(pencil.IsEffectivelyVisible).IsFalse();
            await Assert.That(editor.FindControl<Button>("SaveRenameButton")!.IsEffectivelyVisible).IsTrue();
            await Assert.That(editor.FindControl<Button>("CancelRenameButton")!.IsEffectivelyVisible).IsTrue();

            input.Text = "New name";
            editor.FindControl<Button>("SaveRenameButton")!.Command!.Execute(null);
            await host.SettleUntilAsync(() => pencil.IsEffectivelyVisible, "the pencil back");
            await Assert.That(editor.FindControl<TextBlock>("TitleText")!.Text).IsEqualTo("New name");

            await host.Vm.TeardownAsync();
            return true;
        });
    }
}
