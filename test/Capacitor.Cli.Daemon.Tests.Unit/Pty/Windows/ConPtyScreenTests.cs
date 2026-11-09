using System.Text;
using Capacitor.Cli.Daemon.Pty.Windows;
using Capacitor.Cli.Daemon.Services;
using TUnit.Core.Enums;

namespace Capacitor.Cli.Daemon.Tests.Unit.Pty.Windows;

/// ConPTY renders the console's own buffer and sends its own repaint sequences, not the child's bytes, so
/// text the child erased has to leave the screen copy through whatever ConPTY sent.
[ParallelLimiter<SubprocessLimit>]
public class ConPtyScreenTests {
    // The first powershell.exe a runner starts loads cold: about 7s on an idle windows-latest runner, against under 2s
    // for the next, and a loaded runner has run these rows 8x slower. Only the erase, once the probe is drawn, gets the
    // usual deadline.
    static readonly TimeSpan StartupAllowance = TimeSpan.FromMinutes(2);

    [Test, RunOn(OS.Windows)]
    [Arguments("[Console]::Write(\"`r\" + (' ' * 20))")]
    [Arguments("[Console]::Write(\"`r$e[K\")")]
    [Arguments("[Console]::Write(\"$e[2J$e[H\")")]
    public async Task Text_the_child_erased_leaves_the_screen(string erase) {
        // The erase waits for a key the test sends once the line is on its screen, so one read cannot
        // carry both; ReadKey does not echo, which would move the cursor off the line.
        var script  = $"$e = [char]27; [Console]::Write('kcap-erase-probe'); [void][Console]::ReadKey($true); {erase}; Start-Sleep -Milliseconds 600";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

        await using var proc = ConPtyProcess.Spawn(
            "powershell.exe", ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", encoded],
            AppContext.BaseDirectory, TimeProvider.System);

        var screen = new AnsiScreen(120, 30);
        var seen   = new StringBuilder();
        var drawn  = false;
        using var cts = new CancellationTokenSource(StartupAllowance);
        try {
            await foreach (var chunk in proc.ReadOutputAsync(cts.Token)) {
                screen.Write(chunk);
                seen.Append(Encoding.UTF8.GetString(chunk));
                var shown = screen.Text().Contains("kcap-erase-probe", StringComparison.Ordinal);
                if (shown && !drawn) {
                    drawn = true;
                    cts.CancelAfter(BoundedWait.Deadline);
                    await proc.WriteAsync("x");
                }
                if (drawn && !shown) break;
            }
        } catch (OperationCanceledException) when (cts.IsCancellationRequested) { }

        var sent = seen.ToString().Replace("\x1b", "ESC", StringComparison.Ordinal);
        await Assert.That(drawn).IsTrue().Because($"the probe was not drawn within {StartupAllowance}; ConPTY sent: {sent}");
        await Assert.That(screen.Text()).DoesNotContain("kcap-erase-probe").Because($"ConPTY sent: {sent}");
    }
}
