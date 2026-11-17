using System.Reactive.Linq;
using System.Reactive.Subjects;
using Capacitor.App.Services;
using Capacitor.App.ViewModels;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Remote.Models;
using DynamicData;

namespace Capacitor.App.Tests.Unit.ViewModels;

public class SessionRailProjectTests {
    static readonly string ServerHash = Capacitor.Cli.Core.RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-server");
    static readonly string CliHash = Capacitor.Cli.Core.RepoHashHelper.ComputeRepoHash("kurrent-io", "kcap-cli");
    static readonly RailProject Kcap = new("p-kcap", "kcap", [ServerHash, CliHash]);
    static readonly RailProject Payments = new("p-pay", "payments", ["ffffffffffffffff"]);

    /// The listing names a server row's repository by hash; a daemon row only by owner and name.
    static readonly AgentRow ServerSession = AgentRow.FromServer(new RepoSessionDto {
        SessionId = "server", Status = "ended", AccessLevel = "full",
        StartedAt = DateTimeOffset.UnixEpoch, LastActivityAt = DateTimeOffset.UnixEpoch,
        Repo = new() { Hash = ServerHash },
    }, hostedPresentation: false);
    static readonly AgentRow CliSession = AgentRow.FromLocal(
        new AgentStatusDto("cli", "agent", "claude", "/cli", "Running", null, null, null, DateTime.UnixEpoch, null, null),
        new RepoIdentity("repo:kurrent-io/kcap-cli", "kurrent-io/kcap-cli"));
    static readonly AgentRow DotfilesSession = AgentRow.FromRemote(new AgentInstanceDto {
        AgentId = "dotfiles", Status = "Running", DaemonName = "work-mac", OwnerUserId = "u1", Vendor = "claude",
        RepoOwner = "ana", RepoName = "dotfiles",
    });

    static IEnumerable<string> Shown(SessionRailViewModel rail) =>
        rail.Groups.SelectMany(g => g.Sessions).Select(s => s.Id).Order(StringComparer.Ordinal);

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task Picking_a_project_shows_only_its_repos_and_all_projects_shows_everything() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var projects = new BehaviorSubject<IReadOnlyList<RailProject>>([Kcap, Payments]);
            var persisted = new List<RailPreferences>();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                persistPreferences: persisted.Add, projects: projects);
            directory.Rows.AddOrUpdate([ServerSession, CliSession, DotfilesSession]);
            await Assert.That(rail.ProjectLabel).IsEqualTo(SessionRailViewModel.AllProjectsLabel);

            await rail.SetProjectCommand.Execute(Kcap);
            await Assert.That(Shown(rail)).IsEquivalentTo(["cli", "server"]);
            await Assert.That(rail.ProjectLabel).IsEqualTo("kcap");
            await Assert.That(rail.ProjectInitial).IsEqualTo("K");
            await Assert.That(rail.ProjectOptions.Select(o => (o.Project.Name, o.IsCurrent))).IsEquivalentTo([("kcap", true), ("payments", false)]);
            await Assert.That(persisted[^1].Project).IsEqualTo(Kcap);

            await rail.SetProjectCommand.Execute(null);
            await Assert.That(Shown(rail)).IsEquivalentTo(["cli", "dotfiles", "server"]);
            await Assert.That(persisted[^1].Project).IsNull();
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task A_remembered_project_narrows_before_the_list_loads_and_falls_back_once_it_is_gone() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var projects = new Subject<IReadOnlyList<RailProject>>();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                preferences: RailPreferences.Default with { Project = Kcap }, projects: projects);
            directory.Rows.AddOrUpdate([ServerSession, CliSession, DotfilesSession]);
            await Assert.That(rail.ProjectLabel).IsEqualTo("kcap");
            await Assert.That(Shown(rail)).IsEquivalentTo(["cli", "server"]);
            await Assert.That(rail.HasProjects).IsFalse();

            projects.OnNext([Payments]);
            await Assert.That(rail.Project).IsNull();
            await Assert.That(Shown(rail)).IsEquivalentTo(["cli", "dotfiles", "server"]);
        });
    }

    [Test]
    [NotInParallel("AvaloniaSession")]
    public async Task The_open_session_stays_shown_outside_the_project() {
        await AvaloniaSession.WithImmediateRxScheduler(async () => {
            using var directory = new FakeAgentDirectory();
            using var rail = new SessionRailViewModel(directory, _ => { }, _ => { }, TimeProvider.System,
                preferences: RailPreferences.Default with { Project = Kcap });
            directory.Rows.AddOrUpdate([ServerSession, DotfilesSession]);
            rail.SelectedSessionKey = DotfilesSession.Key;
            await Assert.That(Shown(rail)).IsEquivalentTo(["dotfiles", "server"]);
        });
    }
}
