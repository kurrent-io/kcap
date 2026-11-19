using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using static Capacitor.App.Tests.Unit.AvaloniaSession;

namespace Capacitor.App.Tests.Unit.ViewModels;

[NotInParallel("AvaloniaSession")]
public class SessionTitleEditorViewModelTests {
    sealed class Harness : IDisposable {
        public readonly BehaviorSubject<string> Title = new("Old name");
        public readonly BehaviorSubject<bool> CanRename = new(true);
        public readonly RecordingNotifier Notifier = new();
        public readonly List<string> Calls = [];
        public TaskCompletionSource<SessionRenameResult> Reply = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly SessionTitleEditorViewModel Vm;

        public Harness(string subject = "session") {
            Vm = new SessionTitleEditorViewModel(Title, CanRename, (title, _) => {
                Calls.Add(title);
                return Reply.Task;
            }, Notifier, subject);
        }

        public static bool CanExecute(System.Windows.Input.ICommand command) => command.CanExecute(null);

        public void Dispose() => Vm.Dispose();
    }

    [Test]
    public async Task Enter_with_blank_text_cancels_without_a_call() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "   ";
            await h.Vm.SaveCommand.Execute();

            await Assert.That(h.Calls).IsEmpty();
            await Assert.That(h.Vm.IsEditing).IsFalse();
            await Assert.That(h.Vm.Title).IsEqualTo("Old name");
        });
    }

    [Test]
    public async Task Enter_with_unchanged_text_cancels_without_a_call() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "  Old name ";
            await h.Vm.SaveCommand.Execute();

            await Assert.That(h.Calls).IsEmpty();
            await Assert.That(h.Vm.IsEditing).IsFalse();
        });
    }

    [Test]
    public async Task Applied_save_leaves_edit_mode() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            await Assert.That(h.Vm.Draft).IsEqualTo("Old name");
            await Assert.That(h.Vm.ShowsPencil).IsFalse();
            h.Vm.Draft = "  New name  ";
            h.Reply.SetResult(new SessionRenameResult(true, "New name", null));
            await h.Vm.SaveCommand.Execute();

            await Assert.That(h.Calls).IsEquivalentTo(new[] { "New name" });
            await Assert.That(h.Vm.IsEditing).IsFalse();
            await Assert.That(h.Vm.Title).IsEqualTo("New name");
            await Assert.That(h.Vm.ShowsPencil).IsTrue();
            await Assert.That(h.Notifier.Notified).IsEmpty();
        });
    }

    [Test]
    [Arguments(SessionRenameReasons.NotOwned, "session", "You can't rename a session you don't own")]
    [Arguments(SessionRenameReasons.Refused, "session", "The server kept a newer title")]
    [Arguments(SessionRenameReasons.Transport, "session", "Couldn't rename the session. Check your connection and try again")]
    [Arguments(SessionRenameReasons.Unauthenticated, "session", "Couldn't rename the session. Check your connection and try again")]
    [Arguments(SessionRenameReasons.ServerError, "session", "Couldn't rename the session. Check your connection and try again")]
    [Arguments(SessionRenameReasons.IdentityMismatch, "session", "Couldn't rename the session")]
    [Arguments(null, "work item", "Couldn't rename the work item")]
    public async Task Failed_save_notifies_and_keeps_the_draft(string? reason, string subject, string copy) {
        await RunOnUiAsync(async () => {
            using var h = new Harness(subject);
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "New name";
            h.Reply.SetResult(new SessionRenameResult(false, null, reason));
            await h.Vm.SaveCommand.Execute();

            await Assert.That(h.Notifier.Notified).IsEquivalentTo(new[] { copy });
            await Assert.That(h.Vm.IsEditing).IsTrue();
            await Assert.That(h.Vm.Draft).IsEqualTo("New name");
            await Assert.That(h.Vm.Title).IsEqualTo("Old name");
            await Assert.That(Harness.CanExecute(h.Vm.SaveCommand)).IsTrue();
        });
    }

    [Test]
    public async Task A_throwing_rename_is_a_failure_not_a_crash() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "New name";
            h.Reply.SetException(new InvalidOperationException("boom"));
            await h.Vm.SaveCommand.Execute();

            await Assert.That(h.Notifier.Notified).IsEquivalentTo(new[] { "Couldn't rename the session" });
            await Assert.That(h.Vm.IsEditing).IsTrue();
        });
    }

    [Test]
    public async Task Escape_restores_the_title() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "Half typed";
            await h.Vm.CancelCommand.Execute();

            await Assert.That(h.Vm.IsEditing).IsFalse();
            await Assert.That(h.Vm.Draft).IsEqualTo("Old name");
            await Assert.That(h.Vm.Title).IsEqualTo("Old name");
            await Assert.That(h.Calls).IsEmpty();
        });
    }

    [Test]
    public async Task Buttons_are_disabled_while_saving() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "New name";
            var saving = h.Vm.SaveCommand.Execute().ToTask();

            await Assert.That(h.Vm.IsSaving).IsTrue();
            await Assert.That(h.Vm.IsInputEnabled).IsFalse();
            await Assert.That(Harness.CanExecute(h.Vm.SaveCommand)).IsFalse();
            await Assert.That(Harness.CanExecute(h.Vm.CancelCommand)).IsFalse();
            await Assert.That(h.Vm.ShowsPencil).IsFalse();

            h.Reply.SetResult(new SessionRenameResult(true, null, null));
            await saving;
            await Assert.That(h.Vm.IsSaving).IsFalse();
            await Assert.That(h.Vm.Title).IsEqualTo("New name");
            await Assert.That(h.Vm.ShowsPencil).IsTrue();
        });
    }

    [Test]
    public async Task Losing_eligibility_cancels_the_edit() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            await h.Vm.BeginEditCommand.Execute();
            h.Vm.Draft = "Half typed";
            h.CanRename.OnNext(false);

            await Assert.That(h.Vm.IsEditing).IsFalse();
            await Assert.That(h.Vm.ShowsPencil).IsFalse();
            await Assert.That(Harness.CanExecute(h.Vm.BeginEditCommand)).IsFalse();
        });
    }

    [Test]
    public async Task The_title_follows_its_source() {
        await RunOnUiAsync(async () => {
            using var h = new Harness();
            h.Title.OnNext("Renamed elsewhere");
            await Assert.That(h.Vm.Title).IsEqualTo("Renamed elsewhere");
        });
    }
}
