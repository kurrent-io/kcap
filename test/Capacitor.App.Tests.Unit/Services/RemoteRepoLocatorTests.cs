using Capacitor.App.Services;
using Capacitor.Remote.Models;

namespace Capacitor.App.Tests.Unit.Services;

/// A remote machine reports only folder paths, so its repository is taken from a session that ran
/// in that folder there, and asked for through a local clone's own origin URL where one exists.
public class RemoteRepoLocatorTests {
    static AgentRow Row(string machine, string repoPath, string? owner, string? name, string? ownerUserId = "me", int minutes = 0) {
        var row = AgentRow.FromRemote(new AgentInstanceDto {
            AgentId = Guid.NewGuid().ToString("N"), Status = "Running", RepoPath = repoPath,
            RepoOwner = owner, RepoName = name, DaemonName = machine, OwnerUserId = ownerUserId,
            RegisteredAt = DateTime.UtcNow,
        });
        return row with { CreatedAt = DateTime.UnixEpoch.AddMinutes(minutes) };
    }

    [Test]
    public async Task A_session_in_the_folder_or_its_worktrees_names_the_repository() {
        AgentRow[] rows = [
            Row("box", "/src/app/.capacitor/worktrees/a", "Acme", "app", minutes: 1),
            Row("other", "/src/app", "Else", "where", minutes: 2),
            Row("box", "/src/application", "Wrong", "prefix", minutes: 3),
        ];

        await Assert.That(RemoteRepoLocator.OwnerRepoOn(rows, "box", "me", "/src/app")).IsEqualTo("Acme/app");
    }

    [Test]
    public async Task No_session_or_another_users_session_names_nothing() {
        AgentRow[] rows = [Row("box", "/src/app", null, null), Row("box", "/src/app", "Them", "app", ownerUserId: "them")];

        await Assert.That(RemoteRepoLocator.OwnerRepoOn(rows, "box", "me", "/src/app")).IsNull();
    }

    [Test]
    public async Task A_local_clone_lends_its_origin_url_and_github_is_the_fallback() {
        string?[] origins = [null, "git@gitlab.example:other/repo.git", "git@github.com:Acme/App.git"];

        await Assert.That(RemoteRepoLocator.UrlFor("acme/app", origins)).IsEqualTo("git@github.com:Acme/App.git");
        await Assert.That(RemoteRepoLocator.UrlFor("acme/lib", origins)).IsEqualTo("https://github.com/acme/lib");
    }
}
