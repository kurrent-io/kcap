using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalOutputActivityTests {
    [Test]
    [Arguments("\e(B\x0f\e[?2004h\e[?1000h\e[?1002h\e[?1003h\e[?1006h")]
    [Arguments("\e[?25l\e[H\r\e[39C\e[49B\e[K\e[55;1H\e[53;3H\e[?25h")]
    [Arguments("\e]0;window title\a\e]10;rgb:ffff/ffff/ffff\e\\")]
    [Arguments("\ePterminal payload\e\\\e_hidden payload\e\\")]
    [Arguments("\e^message\e\\\eXstring\e\\\r\n\t \b\x7f")]
    public async Task Housekeeping_does_not_report_task_output_at_any_chunk_boundary(string controls) {
        var bytes = Encoding.UTF8.GetBytes(controls);
        for (var split = 0; split <= bytes.Length; split++) {
            var activity = new TerminalOutputActivity();
            await Assert.That(activity.Observe(bytes.AsSpan(0, split))).IsFalse();
            await Assert.That(activity.Observe(bytes.AsSpan(split))).IsFalse();
            await Assert.That(activity.Observe("real output"u8)).IsTrue();
        }
    }

    [Test]
    [Arguments("new output")]
    [Arguments("\e[32mworking\e[0m")]
    [Arguments("\e[H✻ thinking")]
    [Arguments("\e]title\u0018new output")]
    public async Task Visible_output_is_activity(string text) {
        var activity = new TerminalOutputActivity();
        await Assert.That(activity.Observe(Encoding.UTF8.GetBytes(text))).IsTrue();
    }

    [Test]
    public async Task Parsing_continues_after_text_into_a_trailing_control_sequence() {
        var activity = new TerminalOutputActivity();
        await Assert.That(activity.Observe("text\e]"u8)).IsTrue();
        await Assert.That(activity.Observe("only a title"u8)).IsFalse();
        await Assert.That(activity.Observe("\aactual output"u8)).IsTrue();
    }
}
