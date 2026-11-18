using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core;
using Microsoft.Extensions.Time.Testing;
using TUnit.Assertions.Enums;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

public partial class ChatTabViewModelTests {
    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_prompt_with_files_shows_its_typed_text_and_previews_only_its_images() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var prompt = JsonSerializer.Serialize("look\n\n[Attached files: .attached/b1/shot.png, .attached/b1/notes.pdf]");
            var path = Tmp.CreateFile("attached.jsonl", [UserLine.Replace("\"hello\"", prompt)]);
            await h.PushAsync(Dto(path) with { WorktreePath = Tmp.Path });

            var turn = (UserTurnItem)h.Chat.Items.Single();
            await Assert.That(turn.Text).IsEqualTo("look");
            await Assert.That(turn.Attachments.Select(a => a.FileName)).IsEquivalentTo(new[] { "shot.png", "notes.pdf" }, CollectionOrdering.Matching);
            await Assert.That(turn.Attachments[0].PreviewPath).IsEqualTo(Tmp.PathTo(".attached", "b1", "shot.png"));
            await Assert.That(turn.Attachments[1].PreviewPath).IsNull();

            h.Chat.OpenAttachmentCommand.Execute(turn.Attachments[1]).Subscribe();
            h.Chat.OpenAttachmentCommand.Execute(turn.Attachments[0]).Subscribe();
            await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { Tmp.PathTo(".attached", "b1", "shot.png") });
            await h.TeardownAsync();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Image_previews_wait_for_successful_local_reads() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var read = ReadCallLine.Replace("/repo/x/src/a.cs", "images/a.png");
            var result = ToolResultLine.Replace("t1", "t2");
            var path = Tmp.CreateFile("images.jsonl", [read]);
            try {
                await h.PushAsync(Dto(path) with { WorktreePath = Tmp.Path });
                var call = Group(h.Chat, 0).Calls[0];
                await Assert.That(call.PreviewPath).IsNull();
                File.AppendAllText(path, result + "\n");
                await h.TickAsync();
                await Assert.That(call.PreviewPath).IsEqualTo(Tmp.PathTo("images", "a.png"));
                File.AppendAllText(path, read.Replace("t2", "t3") + "\n" + ToolErrorLine.Replace("t1", "t3") + "\n");
                await h.TickAsync();
                await Assert.That(Group(h.Chat, 0).Calls[^1].PreviewPath).IsNull();
            } finally {
                await h.TeardownAsync();
            }
        });
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    [NotInParallel("AvaloniaSession")]
    public async Task Remote_reads_never_acquire_workstation_previews(bool useWindowsPath) {
        await RunOnUiAsync(async () => {
            var imagePath = useWindowsPath ? @"C:\Users\runneradmin\AppData\Local\Temp\images\a.png" : Tmp.PathTo("a.png");
            var path = Tmp.CreateFile("remote-image.jsonl", [
                ReadCallLine.Replace("\"/repo/x/src/a.cs\"", JsonSerializer.Serialize(imagePath)),
                ToolResultLine.Replace("t1", "t2"),
            ]);
            var time = new FakeTimeProvider();
            using var session = new BehaviorSubject<ChatSessionInfo>(Session("s1") with { Root = Tmp.Path });
            var chat = new ChatTabViewModel(
                "a1", AgentOrigin.Remote, session, Observable.Return<string[]?>(null),
                new AcceptingChatInput(), new NoAttachmentUploader(),
                _ => new LocalTranscriptFeed(path, TranscriptChat.For("claude")!, "a1", time, _ => { }),
                new RecordingOpener(), time, new FakePermissionService(), new SessionRuns(time));
            try {
                await (chat.PendingReadForTesting ?? throw new InvalidOperationException("The session did not start its transcript read."));
                await Assert.That(chat.Items.Count).IsEqualTo(1);
                var call = Group(chat, 0).Calls.Single();
                await Assert.That(call.Outcome).IsEqualTo(ToolOutcome.Done);
                await Assert.That(ToolDetail.ImageFile(call.InputJson)).IsEqualTo(imagePath);
                await Assert.That(call.PreviewPath).IsNull();
                await Assert.That(call.CanOpenFile).IsFalse();
            } finally {
                await chat.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_local_path_opens_only_after_its_warning_is_confirmed() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var target = Tmp.CreateFile("notes.md", "x");
            var path = Tmp.CreateFile("open.jsonl", [ReadCallLine.Replace("/repo/x/src/a.cs", "notes.md")]);
            try {
                await h.PushAsync(Dto(path) with { WorktreePath = Tmp.Path });
                var call = Group(h.Chat, 0).Calls[0];
                await Assert.That(call.CanOpenFile).IsTrue();
                await Assert.That(call.FilePath).IsEqualTo(target);

                call.RequestOpenFile();
                await Assert.That(call.IsConfirmingOpen).IsTrue();
                await Assert.That(call.FileMissing).IsFalse();
                await Assert.That(h.Opener.Opened).IsEmpty();
                call.CancelOpenFile();
                await Assert.That(call.IsConfirmingOpen).IsFalse();
                await Assert.That(h.Opener.Opened).IsEmpty();

                call.RequestOpenFile();
                call.ConfirmOpenFile();
                await Assert.That(call.IsConfirmingOpen).IsFalse();
                await Assert.That(h.Opener.Opened).IsEquivalentTo(new[] { target });
            } finally {
                await h.TeardownAsync();
            }
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_missing_file_is_reported_and_never_opened() {
        await RunOnUiAsync(async () => {
            var h = Claude();
            var target = Tmp.CreateFile("gone.md", "x");
            var path = Tmp.CreateFile("missing.jsonl", [ReadCallLine.Replace("/repo/x/src/a.cs", "gone.md")]);
            try {
                await h.PushAsync(Dto(path) with { WorktreePath = Tmp.Path });
                var call = Group(h.Chat, 0).Calls[0];
                call.RequestOpenFile();
                File.Delete(target);
                call.ConfirmOpenFile();
                await Assert.That(h.Opener.Opened).IsEmpty();
                call.RequestOpenFile();
                await Assert.That(call.FileMissing).IsTrue();
            } finally {
                await h.TeardownAsync();
            }
        });
    }
}
