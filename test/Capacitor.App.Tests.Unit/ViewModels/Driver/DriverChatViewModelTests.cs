using System.Reactive.Linq;
using Capacitor.App.Services;
using Capacitor.App.ViewModels.Driver;

namespace Capacitor.App.Tests.Unit.ViewModels.Driver;

public class DriverChatViewModelTests {
    sealed class FakeLaunch : ILaunchClient {
        public List<LaunchRequest> Requests { get; } = [];

        public Task<LaunchOutcome> StartAsync(LaunchRequest request, CancellationToken ct) {
            Requests.Add(request);
            return Task.FromResult(new LaunchOutcome(true, "agent-1", null));
        }

        public Task<IReadOnlySet<string>?> GetLaunchFeaturesAsync(CancellationToken ct) => Task.FromResult<IReadOnlySet<string>?>(null);
    }

    [Test]
    public async Task The_first_message_starts_an_agent_primed_with_what_Driver_shows() {
        var launch = new FakeLaunch();
        var chat   = new DriverChatViewModel(launch, () => "laptop", () => @"C:\repo", () => "Work item \"Windows-fix factory\" (In review)", null) {
            Draft = "Why is wave 3 not moving?"
        };

        await chat.SendCommand.Execute();

        var request = launch.Requests.Single();
        await Assert.That(request.DaemonName).IsEqualTo("laptop");
        await Assert.That(request.RepoPath).IsEqualTo(@"C:\repo");
        await Assert.That(request.Prompt!).Contains("Windows-fix factory");
        await Assert.That(request.Prompt!).Contains("The person says: Why is wave 3 not moving?");
        await Assert.That(chat.Draft).IsEqualTo("");
    }

    [Test]
    public async Task Without_a_daemon_it_says_so_and_starts_nothing() {
        var launch = new FakeLaunch();
        var chat   = new DriverChatViewModel(launch, () => null, () => @"C:\repo", () => "", null) { Draft = "Hello" };

        await chat.SendCommand.Execute();

        await Assert.That(launch.Requests).IsEmpty();
        await Assert.That(chat.HasError).IsTrue();
    }
}
