using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class RemoteSessionAttachmentTests {
    static AgentInstanceDto Remote(string? title) => new() {
        AgentId = "r1", SessionId = "s2", Status = "Running", DaemonName = "d",
        OwnerUserId = "u7", RegisteredAt = DateTime.UtcNow, Title = title, Prompt = "fix the flaky test",
    };

    /// The remote daemon fetches with its owner's credential, so files are offered only when the
    /// session's daemon is in the signed-in account's daemon listing and its build fetches on dispatched
    /// input. The listing names a daemon kept under a merged alias by that alias, which need not be the
    /// token subject. The session listing's ownership flag plays no part: it can be unset, or stale.
    [Test]
    [Arguments("u7",  "u7",    "1.0.3",        null,  true)]
    [Arguments("u7",  "u7",    "1.0.3-beta.1", false, true)]
    [Arguments("u8",  "u7",    "1.0.3",        null,  true)]
    [Arguments("u7",  "u7",    "1.0.2",        true,  false)]
    [Arguments("u7",  "other", "1.0.3",        true,  false)]
    [Arguments(null,  "u7",    "1.0.3",        true,  false)]
    public async Task A_remote_session_accepts_attachments_only_on_the_signed_in_accounts_capable_daemon(
            string? viewer, string daemonOwner, string version, bool? ownerIsViewer, bool expected) {
        var row = AgentRow.FromRemote(Remote("t")) with { OwnerIsViewer = ownerIsViewer };
        IReadOnlyList<DaemonInfo> daemons = [new DaemonInfo { Name = "d", OwnerUserId = daemonOwner, Version = version }];

        await Assert.That(RemoteSessionViewModel.AcceptsAttachments(row, daemons, viewer)).IsEqualTo(expected);
    }

    /// An attached terminal session submits text alone, so even the viewer's own capable daemon refuses files for it.
    [Test]
    public async Task An_attached_session_never_accepts_attachments() {
        var row = AgentRow.FromRemote(Remote("t")) with { OwnerIsViewer = true, Kind = AgentActionService.AttachedKind };
        IReadOnlyList<DaemonInfo> daemons = [new DaemonInfo { Name = "d", OwnerUserId = "u7", Version = "1.0.3" }];

        await Assert.That(row.IsAttached).IsTrue();
        await Assert.That(RemoteSessionViewModel.AcceptsAttachments(row, daemons, "u7")).IsFalse();
    }
}
