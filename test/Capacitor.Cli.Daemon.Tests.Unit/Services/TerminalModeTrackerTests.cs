using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalModeTrackerTests {
    [Test]
    [Arguments("\e[?1006h", false)]
    [Arguments("\e[?1000;1006h", true)]
    [Arguments("\e[?1000h\e[?1000l", false)]
    [Arguments("\e[?1000h\ec", false)]
    [Arguments("\e[?1000h\e[!p", false)]
    [Arguments("\e]title \e[?1000h\a", false)]
    [Arguments("\eP\e[?1000h\e\\", false)]
    [Arguments("\e[?1000h\e]title \e[?1000l\e\\", true)]
    [Arguments("\e[?1000\u0018h", false)]
    [Arguments("\e[?1000$h", false)]
    [Arguments("\e]cancelled\x18\e[?1000h", true)]
    [Arguments("\e[?2004h", false)]
    public async Task Mouse_modes_survive_chunk_boundaries_and_ignore_unrelated_control_sequences(string output, bool expected) {
        var tracker = new TerminalModeTracker();
        foreach (var b in Encoding.UTF8.GetBytes(output)) tracker.Feed([b]);
        await Assert.That(tracker.IsMouseReporting).IsEqualTo(expected);
    }

    /// <summary>ConPTY's own startup output carries focus reporting and win32-input mode but never
    /// bracketed paste, so only the agent itself can turn this on.</summary>
    [Test]
    [Arguments("\e[?2004h", true)]
    [Arguments("\e[?2004h\e[?2031h\e[?1004h", true)]
    [Arguments("\e[?2031;2004h", true)]
    [Arguments("\e[?9001h\e[?1004h\e[?25l\e[2J\e[m\e[H\e]0;claude\a\e[?25h", false)]
    [Arguments("\e[?2004h\e[?2004l", false)]
    [Arguments("\e[?2004h\e[!p", false)]
    [Arguments("\e]title \e[?2004h\a", false)]
    [Arguments("\e[?1000h", false)]
    public async Task Bracketed_paste_mode_survives_chunk_boundaries(string output, bool expected) {
        var tracker = new TerminalModeTracker();
        foreach (var b in Encoding.UTF8.GetBytes(output)) tracker.Feed([b]);
        await Assert.That(tracker.IsBracketedPasteEnabled).IsEqualTo(expected);
    }
}
