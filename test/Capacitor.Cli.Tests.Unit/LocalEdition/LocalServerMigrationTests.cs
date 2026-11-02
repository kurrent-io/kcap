using Capacitor.Cli.Core;
using Capacitor.Cli.Core.LocalEdition;
using Capacitor.Cli.LocalEdition;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.LocalEdition.Fakes;
using Capacitor.Cli.Tests.Unit.Services;
using Microsoft.Extensions.Time.Testing;

namespace Capacitor.Cli.Tests.Unit.LocalEdition;

/// <summary>The local server follows <c>kcap-service</c> in and out, ending every move with exactly one supervisor, a
/// stopped server still stopped, and a running one ready again.</summary>
public class LocalServerMigrationTests {
    const int Port = 47391;

    [TempDir] public required TempDir Tmp { get; init; }

    readonly FakeServicePipe _pipe = new();
    readonly FakeLocalServerEnrollmentStore _store = new();
    readonly List<string> _log = [];

    UserHome Home => field ??= NewHome();

    LocalRoot Root => field ??= LocalRoot.Resolve(Tmp.PathTo("local"), Home);

    LocalServerUnit Unit => LocalServerUnit.ForInstallation(Root, Port);

    FakeLocalServerService Scheduled => field ??= new(_log, Home);

    LocalServerWindowsService Service => field ??= new(Home, TimeProvider.System, _pipe, _store, Scheduled, (path, content, _) => {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }, _ => null, @"C:\kcap\kcap.exe", _ => { });

    UserHome NewHome() {
        Tmp.CreateDir("home");
        return new(Tmp.GetResolvedPath("home"));
    }

    void Installed() {
        Root.EnsureCreated();
        LocalStateFiles.PublishRegistry(Root,
            InstallRegistry.Empty.WithActive(new("0123456789abcdef0123456789abcdef", Port, $"http://localhost:{Port}", "1.2.3")));
    }

    readonly FakeTimeProvider _time = new();
    readonly List<LocalServerUnit> _awaited = [];

    /// <summary>How long the restarted server takes to answer ready; null when it never does.</summary>
    readonly System.Runtime.CompilerServices.StrongBox<TimeSpan?> _readyAfter = new(TimeSpan.FromSeconds(23));

    Task WaitReady(LocalServerUnit unit, CancellationToken ct) {
        _awaited.Add(unit);

        if (_readyAfter.Value is not { } after)
            throw new LocalLifecycleException($"The local server did not report ready on {unit.BaseUrl} within 10 minutes.");

        _time.Advance(after);
        return Task.CompletedTask;
    }

    Task<LocalServerMove?> ToServiceAsync(bool serviceRunsServers = true) =>
        LocalServerMigration.ToServiceAsync(Root, Home, _time, Scheduled, Service, _store, () => serviceRunsServers, WaitReady, CancellationToken.None);

    Task<LocalServerMove?> ToTaskAsync() => LocalServerMigration.ToTaskAsync(Home, _time, Scheduled, Service, _store, WaitReady, CancellationToken.None);

    [Test]
    public async Task A_server_its_task_runs_moves_to_the_service_running() {
        Installed();
        Scheduled.Install(Unit);

        var said = await ToServiceAsync();

        await Assert.That(said!.Message).Contains("Moved");
        await Assert.That(Scheduled.IsInstalled(Unit)).IsFalse();
        await Assert.That(_store.Current!.Stopped).IsFalse();
        await Assert.That(_pipe.Verbs).Contains(ServicePipeVerbs.ServerEnroll);
    }

    /// <summary>The move restarts the server, so it is reported done only once the server answers again.</summary>
    [Test]
    public async Task A_running_server_moved_to_the_service_is_ready_before_the_move_reports_how_long_it_was_down() {
        Installed();
        Scheduled.Install(Unit);

        var said = await ToServiceAsync();

        await Assert.That(_awaited).IsEquivalentTo([Unit]);
        await Assert.That(said!.Failed).IsFalse();
        await Assert.That(said.Message).Contains("restarted it");
        await Assert.That(said.Message).Contains("down for 23 seconds");
    }

    [Test]
    public async Task A_server_that_does_not_come_back_under_the_service_fails_the_move() {
        Installed();
        Scheduled.Install(Unit);
        _readyAfter.Value = null;

        var said = await ToServiceAsync();

        await Assert.That(said!.Failed).IsTrue();
        await Assert.That(said.Message).Contains("did not come back");
        await Assert.That(said.Message).Contains("did not report ready");
        await Assert.That(_store.Current).IsNotNull();
    }

    [Test]
    public async Task A_stopped_server_moves_to_the_service_stopped() {
        Installed();
        Scheduled.Install(Unit);
        await Scheduled.StopAsync(Unit, CancellationToken.None);

        var said = await ToServiceAsync();

        await Assert.That(_store.Current!.Stopped).IsTrue();
        await Assert.That(_awaited).IsEmpty();
        await Assert.That(said!.Message).Contains("stays stopped");
    }

    /// <summary>The service handed the server back to a task, which restarted it all the same.</summary>
    [Test]
    public async Task A_server_the_service_refuses_is_waited_for_under_its_new_task() {
        Installed();
        Scheduled.Install(Unit);
        _pipe.Answer = verb => verb == ServicePipeVerbs.ServerEnroll ? new ServicePipeResponse(false, Error: "refused") : null;
        _readyAfter.Value = null;

        var said = await ToServiceAsync();

        await Assert.That(_store.Current).IsNull();
        await Assert.That(Scheduled.IsInstalled(Unit)).IsTrue();
        await Assert.That(said!.Failed).IsTrue();
        await Assert.That(said.Message).Contains("Scheduled Task restarted");
    }

    [Test]
    public async Task A_service_that_cannot_run_servers_leaves_the_server_under_its_task() {
        Installed();
        Scheduled.Install(Unit);

        var said = await ToServiceAsync(serviceRunsServers: false);

        await Assert.That(said!.Message).Contains("stays a Scheduled Task");
        await Assert.That(Scheduled.IsInstalled(Unit)).IsTrue();
        await Assert.That(_store.Current).IsNull();
    }

    [Test]
    public async Task Without_a_task_there_is_nothing_to_move() {
        Installed();

        await Assert.That(await ToServiceAsync()).IsNull();
        await Assert.That(_pipe.Verbs).IsEmpty();
    }

    /// <summary>The service keeps the server only while it exists; removing it first would leave no supervisor.</summary>
    [Test]
    public async Task Before_the_service_goes_its_server_moves_back_to_a_task() {
        Installed();
        Service.Install(Unit);
        _log.Clear();

        var said = await ToTaskAsync();

        await Assert.That(said!.Message).Contains("Scheduled Task");
        await Assert.That(_store.Current).IsNull();
        await Assert.That(_pipe.Verbs).Contains(ServicePipeVerbs.ServerUnenroll);
        await Assert.That(_log).IsEquivalentTo(["install"]);
    }

    [Test]
    public async Task A_running_server_moved_back_to_a_task_is_ready_before_the_move_reports_how_long_it_was_down() {
        Installed();
        Service.Install(Unit);

        var said = await ToTaskAsync();

        await Assert.That(_awaited).IsEquivalentTo([Unit]);
        await Assert.That(said!.Failed).IsFalse();
        await Assert.That(said.Message).Contains("restarted it");
        await Assert.That(said.Message).Contains("down for 23 seconds");
    }

    /// <summary>The task keeps the server, since the service is going; only the outcome is a failure.</summary>
    [Test]
    public async Task A_server_that_does_not_come_back_under_its_task_fails_the_move() {
        Installed();
        Service.Install(Unit);
        _readyAfter.Value = null;

        var said = await ToTaskAsync();

        await Assert.That(said!.Failed).IsTrue();
        await Assert.That(said.Message).Contains("did not come back");
        await Assert.That(_store.Current).IsNull();
        await Assert.That(Scheduled.IsInstalled(Unit)).IsTrue();
    }

    [Test]
    public async Task A_server_stopped_under_the_service_stays_stopped_under_its_task() {
        Installed();
        Service.Install(Unit, start: false);
        _log.Clear();

        var said = await ToTaskAsync();

        await Assert.That(_log).IsEquivalentTo(["install", "stop"]);
        await Assert.That(_awaited).IsEmpty();
        await Assert.That(said!.Message).Contains("stays stopped");
    }

    [Test]
    public async Task A_server_the_service_cannot_stop_stays_with_the_service_as_it_was() {
        Installed();
        Service.Install(Unit);
        _pipe.Answer = _ => null;
        _log.Clear();

        await Assert.ThrowsAsync<LocalLifecycleException>(ToTaskAsync);

        await Assert.That(_store.Current!.Stopped).IsFalse();
        await Assert.That(_log).IsEmpty();
    }

    [Test]
    public async Task Without_an_enrollment_there_is_nothing_to_move_back() =>
        await Assert.That(await ToTaskAsync()).IsNull();
}
