using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Capacitor.Cli.Daemon.Harness.Antigravity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Capacitor.Cli.Daemon.Tests.Unit.Harness.Antigravity;

/// <summary>
/// <see cref="AgyTurnProcess"/> against a REAL child — a trivial sleeper, not <c>agy</c>, so this is
/// ungated and runs everywhere. What it pins is the process-lifecycle contract the runtime's
/// confirmed-exit gate is built on, and that contract cannot be observed through a fake: the whole
/// question is whether a KILL has actually landed by the time disposal returns, which only a real
/// process table can answer.
/// </summary>
public class AgyTurnProcessTests {
    /// <summary>
    /// Disposal of a still-running child KILLS it. Exec-per-turn means one child per review round, so
    /// a disposal that only released handles would leak a live <c>agy</c> (and its MCP stdio children)
    /// per round for the life of the daemon.
    ///
    /// <para>Observed through a SECOND handle on the same pid, opened before disposal: the handle
    /// <see cref="AgyTurnProcess"/> owns is disposed by the call under test, so asking it afterwards
    /// would answer from a disposed object rather than from the OS.</para>
    ///
    /// <para><b>What this deliberately does NOT claim</b> is that disposal WAITS for the kill to
    /// land. It cannot: <c>Kill(entireProcessTree: true)</c> is SIGKILL on POSIX, which a child can
    /// neither catch nor defer, so an added bounded wait was measured to be unobservable — the mutant
    /// that deletes it survived 6 of 6 runs of this very test. Exit CONFIRMATION is therefore not
    /// disposal's job at all; it belongs to the caller, before it lets go of the handle.</para>
    /// </summary>
    [Test]
    public async Task Disposing_a_running_turn_child_kills_it() {
        // `ping` rather than `timeout` on Windows: `timeout` exits at once when stdin is not a console.
        var longLived = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd.exe", "/c ping -n 300 127.0.0.1 >NUL")
            : new ProcessStartInfo("/bin/sh", "-c \"sleep 300\"");

        longLived.RedirectStandardInput  = true;
        longLived.RedirectStandardOutput = true;
        longLived.RedirectStandardError  = true;
        longLived.UseShellExecute        = false;
        longLived.CreateNoWindow         = true;

        var child = Process.Start(longLived)!;

        using var observer = Process.GetProcessById(child.Id);
        var turn = new AgyTurnProcess(child, "unread", NullLogger<AgyTurnProcess>.Instance, TimeProvider.System);

        // The precondition, asserted rather than assumed: without a child that is genuinely still
        // running, disposal has nothing to settle and this test proves nothing.
        await Assert.That(turn.HasExited).IsFalse();

        await turn.DisposeAsync();

        // Bounded WAIT, not an immediate read. SIGKILL is delivered asynchronously — the child
        // transitions to zombie some scheduling moment after Kill() returns, and disposal does not
        // block for that — so asserting HasExited on the very next line is a race that a loaded CI
        // box can lose. It passed 14 consecutive local runs, which is exactly the sample size that
        // makes a load-dependent flake look solved; this branch has already been bitten twice by
        // that class. Waiting costs nothing on the happy path and removes the race outright.
        //
        // Waited on the OBSERVER, never on `child`: `child` is the handle AgyTurnProcess just
        // disposed, and touching a disposed Process throws.
        observer.WaitForExit(milliseconds: 5000);

        await Assert.That(observer.HasExited).IsTrue()
            .Because("a disposal that only releases handles leaks one live agy child per review round");
    }

    const string AwkwardPrompt = "/review \"quoted\" back\\slash\nsecond line caf\u00e9 \u65e5\u672c";

    /// <summary>The stdin line is agy's stream-json user message, on one line, in ASCII alone: a raw
    /// newline would split it into two messages, and a raw non-ASCII character is at the mercy of the
    /// code page the child's stdin was opened with.</summary>
    [Test]
    public async Task The_input_line_is_one_ascii_line_carrying_the_prompt_verbatim() {
        var line = AgyTurnProcess.InputLine(AwkwardPrompt);

        await Assert.That(line.All(c => c is >= ' ' and <= '~')).IsTrue();

        using var json = JsonDocument.Parse(line);
        var message    = json.RootElement.GetProperty("message");

        await Assert.That(json.RootElement.GetProperty("event").GetString()).IsEqualTo("user");
        await Assert.That(message.GetProperty("role").GetString()).IsEqualTo("user");
        await Assert.That(message.GetProperty("content").GetString()).IsEqualTo(AwkwardPrompt);
    }

    /// <summary>
    /// A real child reads exactly the input line and then end of input. The end of input matters as
    /// much as the line: agy runs one turn per line until its stdin closes, and exits only then.
    /// <c>sort</c> prints nothing until it has read to the end, so its output is both facts at once.
    /// </summary>
    [Test]
    public async Task A_turn_child_reads_its_prompt_and_then_end_of_input() {
        var echo = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sort.exe"))
            : new ProcessStartInfo("/bin/sh", "-c sort");

        echo.RedirectStandardInput  = true;
        echo.RedirectStandardOutput = true;
        echo.RedirectStandardError  = true;
        echo.UseShellExecute        = false;
        echo.CreateNoWindow         = true;
        echo.StandardInputEncoding  = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        await using var turn = new AgyTurnProcess(
            echo, AwkwardPrompt, NullLogger<AgyTurnProcess>.Instance, TimeProvider.System);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        List<string> lines = [];

        await foreach (var line in turn.ReadLinesAsync(deadline.Token)) lines.Add(line);

        await Assert.That(lines).IsEquivalentTo([AgyTurnProcess.InputLine(AwkwardPrompt)]);
    }
}
