using System.Reactive.Subjects;
using Avalonia.Controls;
using Avalonia.Threading;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.App.Views;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

[NotInParallel("AvaloniaSession")]
public class LocalFrameInterruptPresentationTests {
    const string Note = "Interrupt isn’t available for this session. Stop ends the session.";

    [Test]
    public Task A_working_local_session_explains_the_missing_interrupt_and_clears_it_when_idle() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            await using var rig = new Rig();
            await rig.PushAsync("Starting", false);
            await Assert.That(rig.Chat.ControlNote).IsNull();
            await rig.PushAsync("Running", false);
            await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
            await Assert.That(rig.Chat.ShowsInterrupt).IsFalse();
            await Assert.That(rig.Chat.CanInterruptWithEscape).IsFalse();
            await rig.PushAsync("Running", true);
            await Assert.That(rig.Chat.ControlNote).IsNull();
            await rig.PushAsync("Running", false);
            await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
            await rig.PushAsync("Completed", true);
            await Assert.That(rig.Chat.ControlNote).IsNull();
            await Assert.That(rig.Ops.StopCalls).IsEqualTo(0);
            await Assert.That(rig.Ops.SendTextCalls).IsEqualTo(0);
        });

    [Test]
    public Task A_running_session_with_an_unknown_turn_has_the_same_limit_as_the_missing_button() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            await using var rig = new Rig();
            await rig.PushAsync("Running", null);
            await Assert.That(rig.Chat.AgentStatus.IsRunningUnknownTurn).IsTrue();
            await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
            await Assert.That(rig.Chat.ShowsInterrupt).IsFalse();
        });

    [Test]
    public Task A_pending_agent_request_takes_precedence_over_the_interrupt_explanation() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            await using var rig = new Rig();
            await rig.PushAsync("Running", false);
            await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
            rig.Permissions.Add(PermissionEntries.Entry());
            await WaitUntilAsync(() => rig.Chat.HasPendingCards, what: "the permission card");
            await Assert.That(rig.Chat.ControlNote).IsNull();
            rig.Permissions.Remove("r1");
            await WaitUntilAsync(() => !rig.Chat.HasPendingCards, what: "permission resolved");
            await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
        });

    [Test]
    public Task Connection_and_delivery_recovery_keep_their_own_guidance() => AvaloniaSession.RunOnUiAsync(async () => {
        await using var rig = new Rig();
        await rig.PushAsync("Running", false);
        rig.Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connecting, null, null));
        await Assert.That(rig.Chat.ControlNote).IsNull();
        await Assert.That(rig.Input.Hint).IsEqualTo("Connecting to the agent…");
        rig.Connected();
        await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
        rig.Ops.QueueSendText(new SendTextResult(false, SendTextReasons.Transport, null, null));
        await rig.Input.SendAsync("hello", [], CancellationToken.None);
        await Assert.That(rig.Chat.ControlNote).IsNull();
        await Assert.That(rig.Chat.ComposerHint).IsEqualTo(LocalFrameChatInput.Unconfirmed);
        rig.Input.ConfirmLastSend();
        await Assert.That(rig.Chat.ControlNote).IsEqualTo(Note);
        rig.Presence.OnNext(new AgentPresence(rig.Presence.Value.Dto, true));
        await Assert.That(rig.Chat.ControlNote).IsNull();
    });

    [Test]
    public Task The_explanation_is_readable_in_the_existing_composer_and_hides_for_an_idle_turn() =>
        AvaloniaSession.RunOnUiAsync(async () => {
            await using var rig = new Rig();
            await rig.PushAsync("Running", false);
            var view = new ChatTabView { DataContext = rig.Chat };
            var window = new Window { Content = view, Width = 700, Height = 560 };
            try {
                window.Show();
                Dispatcher.UIThread.RunJobs();
                var note = view.FindControl<TextBlock>("ComposerControlNote")!;
                var interrupt = view.FindControl<Button>("InterruptButton")!;
                await Assert.That(note.IsEffectivelyVisible).IsTrue();
                await Assert.That(note.Text).IsEqualTo(Note);
                await Assert.That(interrupt.IsEffectivelyVisible).IsFalse();
                await rig.PushAsync("Running", true);
                Dispatcher.UIThread.RunJobs();
                await Assert.That(note.IsEffectivelyVisible).IsFalse();
            } finally { window.Close(); }
        });

    sealed class Rig : IAsyncDisposable {
        public FakeDaemonClientService Daemon { get; } = new();
        public ScriptedLocalControlOps Ops { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public BehaviorSubject<AgentPresence> Presence { get; } = new(new(null, false));
        public LocalFrameChatInput Input { get; }
        public ChatTabViewModel Chat { get; }

        public Rig() {
            Connected();
            Input = new LocalFrameChatInput("a1", Daemon, Ops, Presence);
            var time = new FakeTimeProvider();
            Chat = new ChatTabViewModel("a1", Daemon, Input, new NoAttachmentUploader(), TranscriptChat.Journal,
                new RecordingOpener(), time, Permissions, new SessionRuns(time));
        }

        public void Connected() => Daemon.StatusSubject.OnNext(new AttachStatus(AttachState.Connected, null, ["input/1"]));

        public async Task PushAsync(string status, bool? awaitingInput) {
            var dto = Agent("a1", "pi", hasTerminal: false) with { Status = status, AwaitingInput = awaitingInput };
            Daemon.Agents.AddOrUpdate(dto);
            Presence.OnNext(new AgentPresence(dto, status == "Completed"));
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async ValueTask DisposeAsync() {
            await Chat.TeardownAsync();
            Presence.Dispose();
        }
    }
}
