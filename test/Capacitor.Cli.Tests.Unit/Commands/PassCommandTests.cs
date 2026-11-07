using System.Text.RegularExpressions;
using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Http;

namespace Capacitor.Cli.Tests.Unit.Commands;

public class PassCommandTests {
    const string Url = "https://kcap.test";

    sealed record Run(int Exit, string Out, string Error);

    static async Task<Run> PassAsync(FakePassesApi api, bool interactive, params string[] args) {
        var (output, error) = (new StringWriter(), new StringWriter());
        var exit = await new PassCommand(api, Url).HandleAsync(["pass", .. args], new PassTerminal(output, error, interactive, () => "y"));

        return new(exit, output.ToString(), error.ToString());
    }

    /// <summary>With nobody at a terminal, a pass that needs sharing is left for approval in kcap, even though the
    /// reader would have answered yes.</summary>
    [Test]
    public async Task PassCommand_NoTty_DoesNotApprove() {
        var api = new FakePassesApi { SendResult = FakePassesApi.AwaitingApproval("You're sharing this session with Sam.") };

        var run = await PassAsync(api, interactive: false, "Sam", "--session", "s-1", "--brief", "Look at the retry loop");

        await Assert.That(api.Sent.Count).IsEqualTo(1);
        await Assert.That(api.Approved).IsEmpty();
        await Assert.That(api.Declined).IsEmpty();
        await Assert.That(run.Out).Contains($"Waiting for your approval in kcap: {Url}/passes?tab=sent");
        await Assert.That(run.Exit).IsEqualTo(0);
    }

    /// <summary>A send the server fails ends with its message and exit 1, not a crash report, and says the pass may have
    /// been sent: the server may have stored it, and a second send would duplicate it.</summary>
    [Test]
    public async Task PassCommand_SendFails_PrintsServerMessage() {
        var api = new FakePassesApi { SendFails = new CapacitorApiException(500, "Server error") };

        var run = await PassAsync(api, interactive: true, "Sam", "--session", "s-1");

        await Assert.That(run.Error).Contains("Server error");
        await Assert.That(run.Error).Contains($"The pass may have been sent. Check {Url}/passes?tab=sent before passing it again.");
        await Assert.That(run.Exit).IsEqualTo(1);
    }

    /// <summary>A failed approve call may still have landed, or left the pass waiting: the user is told so and where to
    /// check it.</summary>
    [Test]
    public async Task PassCommand_ApprovalFails_SaysPassMayStillAwaitApproval() {
        var api = new FakePassesApi {
            SendResult  = FakePassesApi.AwaitingApproval("You're sharing this session with Sam."),
            AnswerFails = new CapacitorApiException(null, "Cannot reach the server")
        };

        var run = await PassAsync(api, interactive: true, "Sam", "--session", "s-1");

        await Assert.That(api.Approved.Count).IsEqualTo(1);
        await Assert.That(run.Error).Contains("Cannot reach the server");
        await Assert.That(run.Error).Contains($"may still be waiting for approval: {Url}/passes?tab=sent");
        await Assert.That(run.Exit).IsEqualTo(1);
    }

    /// <summary>While the server does not offer passes, or cannot say whether it does (refused login, server error,
    /// unreachable), the command's whole answer is the unknown-command answer, and it asks the server nothing more.</summary>
    [Test]
    [Arguments("not offered")]
    [Arguments("login refused")]
    [Arguments("server error")]
    [Arguments("unreachable")]
    public async Task PassCommand_SurfacesFalse_OutputEqualsUnknownCommand(string surfaces) {
        var api = surfaces switch {
            "not offered"   => new FakePassesApi { Offered = false },
            "login refused" => new FakePassesApi { OfferedFails = new CapacitorApiException(401, "Not logged in") },
            "server error"  => new FakePassesApi { OfferedFails = new CapacitorApiException(500, "Server error") },
            _               => new FakePassesApi { OfferedFails = new CapacitorApiException(null, "Cannot reach the server") }
        };
        var unknown = new StringWriter();
        var unknownExit = UnknownCommand.Write(unknown, "pass");

        var run = await PassAsync(api, interactive: true, "Sam", "--session", "s-1");

        await Assert.That(run.Error).IsEqualTo(unknown.ToString());
        await Assert.That(run.Error).IsEqualTo($"Unknown command: pass{Environment.NewLine}");
        await Assert.That(run.Out).IsEmpty();
        await Assert.That(run.Exit).IsEqualTo(unknownExit);
        await Assert.That(api.Sent).IsEmpty();
    }

    /// <summary>Neither command appears in the command list, and neither has a help page.</summary>
    [Test]
    public async Task PassCommand_OmittedFromHelp() {
        var usage = EmbeddedResources.Load("help-usage.txt");

        await Assert.That(Regex.IsMatch(usage, @"(?m)^\s+(pass|message)\b")).IsFalse();
        await Assert.That(EmbeddedResources.TryLoad("help-pass.txt")).IsNull();
        await Assert.That(EmbeddedResources.TryLoad("help-message.txt")).IsNull();
    }
}
