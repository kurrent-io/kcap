using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;
using DynamicData;
using Microsoft.Extensions.Time.Testing;
using ReactiveUI.Reactive;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit.ViewModels;

/// ChatTabViewModel's tail: phases, the poll, item projection and pairing, path switches and
/// teardown. Every test runs under RunOnUiAsync (the apply hops through Dispatcher.UIThread) and
/// carries [NotInParallel("AvaloniaSession")], like every other VM suite touching the dispatcher.
public partial class ChatTabViewModelTests {
    [TempDir] public required TempDir Tmp { get; init; }

    const string UserLine = """{"type":"user","message":{"role":"user","content":"hello"}}""";
    const string AssistantLine = """{"type":"assistant","message":{"content":[{"type":"text","text":"Hi there"}]}}""";
    const string ToolCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t1","name":"Bash","input":{"command":"ls -la"}}]}}""";
    const string ToolResultLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"ok"}]}}""";
    const string ToolErrorLine = """{"type":"user","message":{"content":[{"type":"tool_result","tool_use_id":"t1","content":"boom","is_error":true}]}}""";
    const string ReadCallLine = """{"type":"assistant","message":{"content":[{"type":"tool_use","id":"t2","name":"Read","input":{"file_path":"/repo/x/src/a.cs"}}]}}""";
    const string NoteLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"content":"<task-notification>\n<summary>Agent finished</summary>\n<result>\nAll good.\n</result>\n</task-notification>"}}""";
    const string ShellFinishLine = """{"type":"user","origin":{"kind":"task-notification"},"message":{"role":"user","content":"<task-notification>\n<task-id>bcyix00ks</task-id>\n<tool-use-id>toolu_S</tool-use-id>\n<status>completed</status>\n<summary>Background command \"Run the full suite\" completed (exit code 0)</summary>\n</task-notification>"}}""";
    static AgentStatusDto Dto(string? transcriptPath, string vendor = "claude") =>
        Agent("a1", vendor, hasTerminal: true, repoPath: "/repo/x") with { TranscriptPath = transcriptPath };

    static ToolGroupItem Group(ChatTabViewModel chat, int index) => (ToolGroupItem)chat.Items[index];
    static PendingCardItem[] CardRows(ChatTabViewModel chat) => [.. chat.Items.OfType<PendingCardItem>()];

    sealed class Harness {
        public FakeDaemonClientService Daemon { get; } = new();
        public FakeTerminalAttachClientFactory Factory { get; } = new();
        public FakeTimeProvider Time { get; } = new();
        public RecordingOpener Opener { get; } = new();
        public FakePermissionService Permissions { get; } = new();
        public SessionRuns Runs { get; }
        public PlanActivity Plan { get; } = new();
        public TerminalTabViewModel Terminal { get; }
        public ChatTabViewModel Chat { get; }

        public Harness(IChatTranscriptProjection? projection, Action<FakePermissionService>? seed = null,
                       ChatInput? input = null, string? unavailableNote = null, IAttachmentUploader? uploader = null,
                       Action<ToolCard>? openCard = null, ComposerDrafts? drafts = null, LocalTranscripts? transcripts = null,
                       IObservable<AccountsSnapshotDto?>? accountReadings = null, IObservable<DateTimeOffset>? usageClock = null,
                       Action<string>? openLink = null) {
            seed?.Invoke(Permissions);
            Runs = new SessionRuns(Time);
            Terminal = new TerminalTabViewModel("a1", Daemon, Factory.Factory, () => new FakeTerminalSurface(), Time);
            Chat = new ChatTabViewModel(
                "a1", Daemon, input ?? new TerminalChatInput(Terminal, "a1", Daemon, new ScriptedLocalControlOps(), Observable.Never<AgentPresence>()), uploader ?? new NoAttachmentUploader(), projection, Opener, Time, Permissions, Runs, unavailableNote, planActivity: Plan, openCard: openCard, drafts: drafts, transcripts: transcripts,
                accountReadings: accountReadings, usageClock: usageClock, openLink: openLink);
        }

        public async Task PushAsync(AgentStatusDto dto) {
            Daemon.Agents.AddOrUpdate(dto);
            await (Terminal.PendingResolveWorkForTesting ?? Task.CompletedTask);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TickAsync() {
            Time.Advance(ChatTabViewModel.PollInterval);
            await (Chat.PendingReadForTesting ?? Task.CompletedTask);
        }

        public async Task TeardownAsync() {
            await Chat.TeardownAsync();
            await Terminal.TeardownAsync();
        }
    }

    static Harness Claude(Action<FakePermissionService>? seed = null, Action<ToolCard>? openCard = null) =>
        new(TranscriptChat.For("claude"), seed, openCard: openCard);

    sealed class GatedProjection(ITranscriptProjection inner, string blockOn, TaskCompletionSource gate) : ITranscriptProjection {
        public TranscriptContext CreateContext(string sessionId, string? agentId) => inner.CreateContext(sessionId, agentId);

        public ProjectionResult Project(string line, int lineNumber, DateTimeOffset receivedAt, TranscriptContext context) {
            if (line.Contains(blockOn, StringComparison.Ordinal)) gate.Task.GetAwaiter().GetResult();
            return inner.Project(line, lineNumber, receivedAt, context);
        }
    }

    /// A scripted ChatInput for composer tests: SendAsync completes when the test says so.
    sealed class ScriptedInput : ChatInput {
        public override string? SenderUserId => "u2";
        public TaskCompletionSource<ChatSendOutcome>? Pending;
        public int Disposals;
        public bool CanAttachValue = true;
        /// Off for a channel whose send has already gone out when the tab is torn down.
        public bool HonoursCancellation = true;
        public List<(string Text, IReadOnlyList<string> Ids, CancellationToken Ct)> Sends { get; } = [];
        public override SendAvailability Availability => Pending is null ? SendAvailability.Ready : SendAvailability.Sending;
        public override bool CanAcceptText => Pending is null;
        public override string Hint => "scripted";
        public override bool CanAttach => CanAttachValue;
        public override string? AttachHint => CanAttachValue ? null : "attachments need the daemon updated";
        public bool CanResubmitValue;
        public bool ResubmitResult = true;
        public int Resubmits;
        public override bool CanResubmit => CanResubmitValue;
        public override Task<bool> ResubmitAsync(CancellationToken ct) {
            Resubmits++;
            return Task.FromResult(ResubmitResult);
        }
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) {
            Sends.Add((text, attachmentIds, ct));
            Pending = new TaskCompletionSource<ChatSendOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            this.RaisePropertyChanged(nameof(CanAcceptText));
            return Pending.Task.ContinueWith(t => { Pending = null; this.RaisePropertyChanged(nameof(CanAcceptText)); return t.Result; }, HonoursCancellation ? ct : CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        public override void Dispose() => Disposals++;
    }

    static string User(string text) => UserLine.Replace("hello", text);

    static async Task SendAsync(Harness h, ScriptedInput input, string text) {
        h.Chat.ComposerText = text;
        var send = h.Chat.SendCommand.Execute().ToTask();
        input.Pending!.SetResult(ChatSendOutcome.Accepted);
        await send;
    }

    static AgentStatusDto Hosted(string path, string status, bool? awaitingInput) =>
        Agent("a1", "pi", hasTerminal: false) with {
            TranscriptPath = path, TranscriptFormat = TranscriptFormats.Envelopes, Status = status, AwaitingInput = awaitingInput,
        };

    sealed class HoldingUploader : IAttachmentUploader {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<UploadOutcome> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<UploadOutcome> UploadAsync(IReadOnlyList<StagedAttachment> files, CancellationToken ct) {
            Started.TrySetResult();
            return Release.Task;
        }
    }

    /// A channel that takes every send, for the queue tests: the transcript, not the channel,
    /// is what retires a message.
    class AcceptingChatInput(string? senderUserId = "u2") : ChatInput {
        public override string? SenderUserId => senderUserId;
        public override SendAvailability Availability => SendAvailability.Ready;
        public override bool CanAcceptText => true;
        public override string Hint => "";
        public override bool CanAttach => true;
        public override string? AttachHint => null;
        public override Task<ChatSendOutcome> SendAsync(string text, IReadOnlyList<string> attachmentIds, CancellationToken ct) => Task.FromResult(ChatSendOutcome.Accepted);
        public override void Dispose() { }
    }

    /// Serves one Reset read on demand — the pane opens its feed and reads once during
    /// construction, so a feed that resets on its first read would spend it before the test.
    sealed class ScriptedFeed : IChatTranscriptFeed {
        public bool ResetNext;
        public string? FailNext;

        public FeedRead ReadAppended() {
            if (FailNext is { } failure) {
                FailNext = null;
                return new(FeedStatus.Failed, [], Failure: failure);
            }
            if (!ResetNext) return new(FeedStatus.Ok, []);
            ResetNext = false;
            return new(FeedStatus.Reset, []);
        }

        public long? CurrentOffset => 0;
        public void Dispose() { }
    }

    static ChatSessionInfo Session(string? feedKey) => new("Running", "Running", "gemini", null, null, null, false, "", feedKey);
}
