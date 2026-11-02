using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Reactive.Threading.Tasks;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;
using static Capacitor.App.Tests.Unit.WorkspaceFixtures;

namespace Capacitor.App.Tests.Unit;

/// A card the viewer has no right to answer is read-only: its answering commands cannot run, nothing reaches the
/// service, and it says who it waits on. A change of rights reopens it.
[NotInParallel(nameof(AvaloniaSession))]
public class PendingCardViewModelTests {
    public enum Kind { Permission, AcpPermission, Question, AcpQuestion }

    static (PendingCardViewModel Card, Func<IObservable<bool>> Answer, string Waiting) Make(
            Kind kind, IPermissionService permissions, IObservable<AgentViewerAccess> access) {
        var root = new BehaviorSubject<string?>(null);
        switch (kind) {
            case Kind.Permission: {
                var card = new PermissionCardViewModel(PermissionEntries.ServerEntry(), permissions, root, access);
                return (card, () => card.AllowCommand.CanExecute, "Waiting for the owner to decide");
            }
            case Kind.AcpPermission: {
                var card = new PermissionCardViewModel(PermissionEntries.AcpPermission(), permissions, root, access);
                return (card, () => card.Options[0].PickCommand.CanExecute, "Waiting for the owner to decide");
            }
            case Kind.Question: {
                var card = new QuestionCardViewModel(PermissionEntries.ServerQuestion(), permissions, access);
                return (card, () => card.Questions[0].Options[0].PickCommand.CanExecute, "Waiting for the owner to answer");
            }
            default: {
                var card = new AcpQuestionCardViewModel(PermissionEntries.AcpQuestion(), permissions, access);
                return (card, () => card.Options[0].PickCommand.CanExecute, "Waiting for the owner to answer");
            }
        }
    }

    [Test]
    [Arguments(Kind.Permission)]
    [Arguments(Kind.AcpPermission)]
    [Arguments(Kind.Question)]
    [Arguments(Kind.AcpQuestion)]
    public async Task A_card_without_the_right_is_read_only_until_the_right_arrives(Kind kind) {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var permissions = new FakePermissionService();
            var rights = new BehaviorSubject<AgentViewerAccess>(AgentViewerAccess.From(new AgentViewerRightsDto { Reaches = true }));
            var (card, answer, waiting) = Make(kind, permissions, rights);
            using (card) {
                await Assert.That(card.CanAnswer).IsFalse();
                await Assert.That(card.WaitingOn).IsEqualTo(waiting);
                await Assert.That(await answer().FirstAsync()).IsFalse();

                rights.OnNext(AgentViewerAccess.From(new AgentViewerRightsDto { Reaches = true, AnswerQuestions = true, ApproveToolUse = true }));
                await Assert.That(card.CanAnswer).IsTrue();
                await Assert.That(card.WaitingOn).IsNull();
                await Assert.That(await answer().FirstAsync()).IsTrue();
            }
        });
    }

    [Test]
    public async Task A_read_only_card_never_reaches_the_service() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var permissions = new FakePermissionService();
            var none = Observable.Return(AgentViewerAccess.From(new AgentViewerRightsDto { Reaches = true }));
            using var question = new AcpQuestionCardViewModel(PermissionEntries.AcpQuestion(), permissions, none);
            using var permission = new PermissionCardViewModel(PermissionEntries.ServerEntry(), permissions, new BehaviorSubject<string?>(null), none);
            permissions.Queue(PermissionResolveKind.Applied);
            permissions.Queue(PermissionResolveKind.Applied);
            await question.Options[0].PickCommand.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(5));
            await permission.DenyCommand.Execute().ToTask().WaitAsync(TimeSpan.FromSeconds(5));
            await Assert.That(permissions.AcpAnswered).IsEmpty();
            await Assert.That(permissions.Resolved).IsEmpty();
        });
    }

    /// The local lane is this machine's own daemon, so a remote agent's rights never bind its cards.
    [Test]
    public async Task Rights_bind_only_server_lane_cards() {
        await AvaloniaSession.RunOnUiAsync(async () => {
            using var permissions = new FakePermissionService();
            var none = Observable.Return(AgentViewerAccess.From(new AgentViewerRightsDto { Reaches = true }));
            using var cards = new PendingCardsViewModel("a1", AgentOrigin.Local, Observable.Return<string?>("s1"), permissions,
                Observable.Return<string?>(null), access: none);
            var local = PermissionEntries.Entry("l1", "a1");
            var server = PermissionEntries.ServerEntry("srv-1", sessionId: "s1");
            server.AgentId = "a1";
            permissions.Add(local);
            permissions.Add(server);
            await WaitUntilAsync(() => cards.PendingCards.Count == 2, what: "both cards");
            await Assert.That(cards.PendingCards.Single(c => c.Key == local.Key).CanAnswer).IsTrue();
            await Assert.That(cards.PendingCards.Single(c => c.Key == server.Key).CanAnswer).IsFalse();
        });
    }
}
