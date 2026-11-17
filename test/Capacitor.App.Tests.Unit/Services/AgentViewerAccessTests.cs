using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.Services;

/// The server's rights for a hosted agent, read into what the viewer may do. No rights is a server with agent
/// privileges off (or one that predates them), where nothing changes.
public class AgentViewerAccessTests {
    [Test]
    public async Task No_rights_are_unrestricted() {
        var access = AgentViewerAccess.From(null);
        await Assert.That(access).IsEqualTo(AgentViewerAccess.Unrestricted);
        await Assert.That(access.HasRights).IsFalse();
        await Assert.That(access.CanSend).IsTrue();
        await Assert.That(access.CanPressKeys).IsTrue();
        await Assert.That(access.InputHeld).IsFalse();
        await Assert.That(access.ControlRefusal).IsNull();
        await Assert.That(access.ApprovalMode).IsNull();
        await Assert.That(access.CanAnswer(PermissionEntries.ServerEntry())).IsTrue();
        await Assert.That(access.CanAnswer(PermissionEntries.AcpQuestion())).IsTrue();
    }

    [Test]
    [Arguments(true, false, false, false, false, true, true, false)]
    [Arguments(false, true, false, false, false, true, false, true)]
    [Arguments(false, true, false, false, true, true, true, false)]
    [Arguments(false, true, false, false, false, false, true, false)]
    [Arguments(false, false, true, true, true, true, false, false)]
    public async Task Rights_decide_sending_keys_and_holding(
            bool owner, bool reaches, bool answer, bool approve, bool sendsDirect, bool hold,
            bool pressesKeys, bool held) {
        var access = AgentViewerAccess.From(new AgentViewerRightsDto {
            IsOwner = owner, Reaches = reaches, AnswerQuestions = answer, ApproveToolUse = approve,
            SendWithoutApproval = sendsDirect, HoldMessages = hold,
        });
        await Assert.That(access.HasRights).IsTrue();
        await Assert.That(access.CanSend).IsEqualTo(owner || reaches);
        await Assert.That(access.CanPressKeys).IsEqualTo(pressesKeys);
        await Assert.That(access.InputHeld).IsEqualTo(held);
        await Assert.That(access.ControlRefusal).IsEqualTo(
            !(owner || reaches) ? AgentViewerAccess.ViewOnlyReason : held ? AgentViewerAccess.HeldReason : null);
    }

    /// Answering a question needs Answer questions, a permission Approve tool use, and neither counts without reach.
    [Test]
    [Arguments(true, true, true, true, true)]
    [Arguments(true, true, false, true, false)]
    [Arguments(true, false, true, false, true)]
    [Arguments(false, true, true, false, false)]
    public async Task Each_prompt_kind_needs_its_own_right(bool reaches, bool answer, bool approve, bool answersQuestion, bool approvesTool) {
        var access = AgentViewerAccess.From(new AgentViewerRightsDto { Reaches = reaches, AnswerQuestions = answer, ApproveToolUse = approve });
        var question = PermissionEntries.AcpQuestion();
        var permission = PermissionEntries.ServerEntry();
        await Assert.That(access.CanAnswer(question)).IsEqualTo(answersQuestion);
        await Assert.That(access.CanAnswer(permission)).IsEqualTo(approvesTool);
        await Assert.That(access.WaitingOn(question)).IsEqualTo(answersQuestion ? null : "Waiting for the owner to answer");
        await Assert.That(access.WaitingOn(permission)).IsEqualTo(approvesTool ? null : "Waiting for the owner to decide");
    }
}
