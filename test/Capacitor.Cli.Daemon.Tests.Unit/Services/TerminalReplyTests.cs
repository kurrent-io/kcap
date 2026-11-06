using System.Text;
using Capacitor.Cli.Daemon.Services;

namespace Capacitor.Cli.Daemon.Tests.Unit.Services;

public class TerminalReplyTests {
    [Test]
    [Arguments("\x1b[12;40R")]
    [Arguments("\x1b[0n")]
    [Arguments("\x1b[?62;22c")]
    [Arguments("\x1b[>0;276;0c")]
    [Arguments("\x1b[?2004;1$y")]
    [Arguments("\x1b[?1u")]
    [Arguments("\x1b[8;40;120t")]
    [Arguments("\x1b[I")]
    [Arguments("\x1b[O")]
    [Arguments("\x1b]11;rgb:0000/0000/0000\x07")]
    [Arguments("\x1b]10;rgb:ffff/ffff/ffff\x1b\\")]
    [Arguments("\x1bP>|xterm(390)\x1b\\")]
    [Arguments("\x1b[I\x1b[12;40R")]
    public async Task A_terminal_reply_is_recognised(string data) =>
        await Assert.That(TerminalReply.IsOnlyReplies(Encoding.UTF8.GetBytes(data))).IsTrue();

    /// <summary>What a key or mouse action encodes to, alone or after a reply, is not one.</summary>
    [Test]
    [Arguments("x")]
    [Arguments("\r")]
    [Arguments("\x1b")]
    [Arguments("\x1b[A")]
    [Arguments("\x1b[3~")]
    [Arguments("\x1b[97;5u")]
    [Arguments("\x1b[<64;10;5M")]
    [Arguments("\x1b[200~text\x1b[201~")]
    [Arguments("\x1b[12;40Rx")]
    [Arguments("\x1b]11;unterminated")]
    [Arguments("")]
    public async Task Typed_input_is_not_a_reply(string data) =>
        await Assert.That(TerminalReply.IsOnlyReplies(Encoding.UTF8.GetBytes(data))).IsFalse();
}
