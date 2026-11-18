using Capacitor.Cli.Commands;
using Capacitor.Cli.Core;
using Capacitor.Cli.Core.Accounts;
using Capacitor.Cli.Core.LocalIpc;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Tests.Unit.Commands;

/// <summary><c>kcap agent stop</c> against a control socket standing in for the daemon.</summary>
[ExcludeOn(OS.Windows)] // the control socket is a Unix-domain socket
public class AgentCommandStopTests {
    [TempDaemonPaths] public required TempDaemonStore Daemons { get; init; }
    [TempConfigRoot]  public required TempConfigRoot  Config  { get; init; }
    [TempHome]        public required TempHome        Home    { get; init; }

    const string Steered = "mod-0123456789abcdef";
    const string Hosted  = "a1b2c3d4";

    static readonly string Table = string.Join('\n',
        $"{Hosted}\tRunning\t/r1\tagent\t\t\tFix the login\trunning",
        $"{Steered}\tRunning\t\tattached\t\t\t\tattached");

    StringWriter Out { get; } = new() { NewLine = "\n" };
    StringWriter Err { get; } = new() { NewLine = "\n" };

    AgentCommand Command(TextReader? input = null) =>
        new(Daemons.Store, AccountStore.Beside(Daemons.Store), Config.Root, Resolutions.None(Config.Root), Home, TestHarnesses.All(),
            TestBinaries.None, new WorkingDirectory(Daemons.Root), TimeProvider.System, new StandardStreams(Out, Err, input ?? TextReader.Null));

    static LocalFrame List => new(FrameType.AgentList) { Text = Table };

    async Task<string> StopAsync(string daemon, IReadOnlyList<LocalFrame?> replies, params string[] args) {
        Out.GetStringBuilder().Clear();
        var received = FakeControlSocket.AnswerInOrder(Daemons.Store, daemon, replies);

        await Assert.That(await Command().HandleAsync(["agent", "stop", .. args, "--daemon", daemon], baseUrl: null)).IsEqualTo(0);
        await received.WaitAsync(TimeSpan.FromSeconds(30));

        return Out.ToString();
    }

    /// <summary>The daemon steers an attached session but doesn't host it, so no stop of one may read as stopping
    /// the agent, and <c>stop --all</c> keeps leaving it alone without <c>--force</c>.</summary>
    [Test]
    public async Task Stopping_an_attached_session_reads_as_disconnecting_steering() {
        var one = await StopAsync("one", [List, LocalFrame.StopAck($"{Steered}\tstopped")], Steered);

        await Assert.That(one).IsEqualTo($"Disconnected steering from {Steered}. Its Claude Code session keeps running in its terminal.\n");

        var all = await StopAsync("all", [List, LocalFrame.StopAck($"{Hosted}\tstopped")], "--all", "-y");

        await Assert.That(all).Contains($"  • {Hosted}  /r1");
        await Assert.That(all).Contains(
            "Leaving 1 steered terminal session(s) alone — pass --force to disconnect steering from them; "
          + "their Claude Code sessions keep running:");
        await Assert.That(all).Contains($"  • {Steered}\n");
        await Assert.That(all).Contains($"Stopped {Hosted}.");
        await Assert.That(all).DoesNotContain("review agent");
        await Assert.That(all).DoesNotContain($"Stopped {Steered}");

        var forced = await StopAsync("forced",
            [List, LocalFrame.StopAck($"{Hosted}\tstopped"), LocalFrame.StopAck($"{Steered}\tstopped")], "--all", "-y", "--force");

        await Assert.That(forced).Contains("Will disconnect steering from 1 terminal session(s); their Claude Code sessions keep running:");
        await Assert.That(forced).Contains($"Disconnected steering from {Steered}. Its Claude Code session keeps running in its terminal.");
        await Assert.That(forced).Contains($"Stopped {Hosted}.");
        await Assert.That(forced).DoesNotContain("review agent");
        await Assert.That(forced).DoesNotContain($"Stopped {Steered}");
    }

    [Test]
    public async Task Stop_all_with_only_steered_sessions_says_what_force_would_do() {
        var text = await StopAsync("only", [new LocalFrame(FrameType.AgentList) { Text = $"{Steered}\tRunning\t\tattached\t\t\t\tattached" }],
            "--all", "-y");

        await Assert.That(text).IsEqualTo(
            "No agents to stop. 1 steered terminal session(s) left alone — pass --force to disconnect steering from them; "
          + "their Claude Code sessions keep running.\n");
    }

    [Test]
    public async Task The_confirmation_names_what_happens_to_each() {
        Out.GetStringBuilder().Clear();
        _ = FakeControlSocket.AnswerInOrder(Daemons.Store, "ask", [List]);
        using var input = new StringReader("n\n");
        await Assert.That(await Command(input).HandleAsync(["agent", "stop", "--all", "--force", "--daemon", "ask"], baseUrl: null)).IsEqualTo(0);

        await Assert.That(Out.ToString()).Contains(
            "Will disconnect steering from 1 terminal session(s); their Claude Code sessions keep running:\n"
          + $"  • {Steered}\n"
          + "Stop 1 and disconnect steering from 1? [y/N] Cancelled.");
        await Assert.That(Out.ToString()).DoesNotContain("Disconnected");
    }
}
