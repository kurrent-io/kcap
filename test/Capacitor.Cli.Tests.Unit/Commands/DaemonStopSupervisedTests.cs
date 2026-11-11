using Capacitor.Cli.Commands;
using Capacitor.Cli.Core.LocalIpc;
using Capacitor.Cli.Services;
using Capacitor.Cli.Tests.Unit.Services;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>daemon stop</c> of a supervised daemon goes through a user-level manager, since a raw stop would be
/// relaunched; under kcap-service it is refused, and the refusal exits nonzero so a script does not read the
/// still-running daemon as stopped.</summary>
public class DaemonStopSupervisedTests {
    static readonly ShutdownRequestDto End  = new("end", "final");
    static readonly ShutdownRequestDto Keep = new("keep", "final");

    [TempConfigRoot] public required TempConfigRoot Config { get; init; }

    readonly System.Text.StringBuilder _out   = new();
    readonly System.Text.StringBuilder _error = new();
    readonly List<(string Id, string[] Args)> _calls = [];

    /// <summary>Records its call and stops the unit, as <c>daemon service stop</c> does.</summary>
    Task<int> ServiceStop(IServiceManager manager, string id, string[] args) {
        _calls.Add((id, args));
        return Task.FromResult(manager.Stop(id, out _) ? 0 : 1);
    }

    Task<int?> StopAsync(IServiceManager? manager, string name, ShutdownRequestDto request) =>
        DaemonCommands.StopSupervisedAsync(
            manager, name, request, new StandardStreams(new StringWriter(_out), new StringWriter(_error), TextReader.Null), ServiceStop);

    [Test]
    public async Task A_daemon_under_a_user_level_manager_is_stopped_through_it() {
        var manager = new RecordingServiceManager(new() { ["sandbox"] = "/opt/kcap/kcap-daemon" });

        var exit = await StopAsync(manager, "sandbox", End);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(manager.Stopped).IsEquivalentTo(["sandbox"]);
        await Assert.That(_calls.Single().Args).IsEmpty();
        await Assert.That(_out.ToString()).Contains("Stopped daemon 'sandbox' through its fake; it starts again at sign-in");
        await Assert.That(_error.ToString()).IsEmpty();
    }

    /// <summary>The service verbs resolve a name through the same sanitizing, so the stop reaches the id they find.</summary>
    [Test]
    public async Task The_service_stop_names_the_service_id_and_keeps_the_agents_when_asked() {
        var manager = new RecordingServiceManager(new() { ["my-laptop"] = "/opt/kcap/kcap-daemon" });

        var exit = await StopAsync(manager, "My Laptop", Keep);

        await Assert.That(exit).IsEqualTo(0);
        await Assert.That(_calls.Single().Id).IsEqualTo("my-laptop");
        await Assert.That(_calls.Single().Args).IsEquivalentTo(["--keep-agents"]);
    }

    /// <summary>kcap-service counts as supervising even when it does not answer, so the stop is refused rather than
    /// sent to a daemon it would relaunch.</summary>
    [Test]
    public async Task A_daemon_under_kcap_service_is_refused_with_a_nonzero_exit_and_the_service_command() {
        var enrollments = new FakeServiceEnrollmentStore { Current = new ServiceEnrollment("my-laptop", "C:/kcap/kcap-daemon.exe", null, null, null) };
        var kcapService = new WindowsServiceManager(Config.Root, new FakeServicePipe { Answer = _ => null }, enrollments, tasks: null);

        var exit = await StopAsync(kcapService, "My Laptop", Keep);

        await Assert.That(exit).IsEqualTo(1);
        await Assert.That(_calls).IsEmpty();
        await Assert.That(_error.ToString()).Contains("is managed by Windows service kcap-service; a raw stop would be auto-restarted.");
        await Assert.That(_error.ToString()).Contains("Use: kcap daemon service stop --name my-laptop --keep-agents  (or uninstall");
    }

    [Test]
    public async Task An_unsupervised_daemon_is_left_to_the_direct_stop() {
        var manager = new RecordingServiceManager(new() { ["other"] = "/opt/kcap/kcap-daemon" });

        await Assert.That(await StopAsync(manager, "sandbox", End)).IsNull();
        await Assert.That(await StopAsync(null, "sandbox", End)).IsNull();
        await Assert.That(_calls).IsEmpty();
        await Assert.That(manager.Stopped).IsEmpty();
        await Assert.That(_error.ToString()).IsEmpty();
    }
}
